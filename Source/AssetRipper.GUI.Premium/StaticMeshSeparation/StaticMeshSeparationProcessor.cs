using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Import.Logging;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated.Classes.ClassID_23;
using AssetRipper.SourceGenerated.Classes.ClassID_25;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Enums;
using AssetRipper.SourceGenerated.Extensions;

namespace AssetRipper.GUI.Premium.StaticMeshSeparation;

/// <summary>
/// Reverses Unity's static batching.
/// </summary>
/// <remarks>
/// When a scene is built, Unity merges every mesh used by a statically marked GameObject into one
/// large "Combined Mesh (root scene)" and gives each renderer a <c>StaticBatchInfo</c> describing
/// which sub-meshes of that combined mesh belong to it. The original meshes and object names are
/// lost in the process.
/// <para>
/// This processor walks the static-batched renderers, cuts the sub-mesh ranges they claim back out
/// of the combined mesh into standalone <see cref="IMesh"/> assets, points the renderers at those
/// instead, and clears the batch info so the objects come out as independent meshes again.
/// </para>
/// <para>
/// Renderers that share an identical claim on the same combined mesh share one generated mesh, so
/// duplicates are collapsed even when they appear in different scenes.
/// </para>
/// </remarks>
internal sealed class StaticMeshSeparationProcessor : IAssetProcessor
{
	/// <summary>
	/// The sub-mesh indices of a combined mesh that a single renderer claims.
	/// </summary>
	private readonly record struct BatchClaim(IMeshRenderer Renderer, int[] SubMeshIndices);

	public void Process(GameData gameData)
	{
		List<BatchClaim> claims = CollectClaims(gameData);

		if (claims.Count == 0)
		{
			Logger.Info(LogCategory.Processing, "No statically batched meshes were found.");
			return;
		}

		ProcessedBundle processedBundle = gameData.GameBundle.AddNewProcessedBundle("Separated Static Meshes");
		ProcessedAssetCollection meshCollection = processedBundle.AddNewProcessedCollection("Separated Meshes", gameData.ProjectVersion);

		// Meshes generated during this run, keyed by the combined mesh they came from and the
		// sub-mesh indices that were cut out. This is what lets identical instances - including
		// ones spread across different scenes - share a single generated mesh.
		Dictionary<(IMesh Source, string Key), IMesh> generatedMeshes = new();

		// Reading and splitting a combined mesh is the expensive part, so each one is only
		// decompressed once.
		Dictionary<IMesh, MeshData?> combinedMeshCache = new();

		int separatedCount = 0;
		int generatedMeshCount = 0;
		int failureCount = 0;

		foreach (IGrouping<IMesh, BatchClaim> group in claims.GroupBy(static c => c.Renderer.GetMesh()!))
		{
			IMesh combinedMesh = group.Key;

			if (!combinedMeshCache.TryGetValue(combinedMesh, out MeshData? source))
			{
				source = MeshData.TryMakeFromMesh(combinedMesh, out MeshData meshData) ? meshData : null;
				combinedMeshCache.Add(combinedMesh, source);

				if (source is null)
				{
					Logger.Warning(LogCategory.Processing, $"Could not read the vertex data of combined mesh '{combinedMesh.Name}'. It will be left as-is.");
				}
			}

			if (source is not MeshData sourceMeshData)
			{
				failureCount += group.Count();
				continue;
			}

			BatchClaim[] groupClaims = group.ToArray();

			foreach (BatchClaim claim in groupClaims)
			{
				// A renderer that claims the entire mesh and is the only one using it has nothing
				// to separate: the mesh it points at is already its own.
				if (groupClaims.Length == 1 && IsWholeMesh(claim, sourceMeshData))
				{
					continue;
				}

				try
				{
					if (!TryValidate(claim, sourceMeshData, out string? error))
					{
						Logger.Warning(LogCategory.Processing, $"Skipping static mesh separation for '{claim.Renderer.GetGameObjectName()}': {error}");
						failureCount++;
						continue;
					}

					// Sorted so that two renderers claiming the same sub-meshes in a different
					// order still share one generated mesh.
					string key = string.Join(',', claim.SubMeshIndices.Order());

					if (!generatedMeshes.TryGetValue((combinedMesh, key), out IMesh? separatedMesh))
					{
						separatedMesh = CreateSeparatedMesh(meshCollection, sourceMeshData, claim);
						generatedMeshes.Add((combinedMesh, key), separatedMesh);
						generatedMeshCount++;
					}

					claim.Renderer.AssignMesh(separatedMesh);
					separatedCount++;
				}
				catch (Exception exception)
				{
					Logger.Error(LogCategory.Processing, $"Failed to separate the static mesh of '{claim.Renderer.GetGameObjectName()}'.", exception);
					failureCount++;
				}
			}
		}

		Logger.Info(LogCategory.Processing, $"Static mesh separation: {separatedCount} renderer(s) separated into {generatedMeshCount} mesh(es), {failureCount} skipped.");
	}

	/// <summary>
	/// Finds every renderer that is part of a static batch and records which sub-meshes of its
	/// mesh belong to it.
	/// </summary>
	private static List<BatchClaim> CollectClaims(GameData gameData)
	{
		List<BatchClaim> claims = [];

		foreach (IMeshRenderer renderer in gameData.GameBundle.FetchAssets().OfType<IMeshRenderer>())
		{
			IRenderer baseRenderer = renderer;

			if (!baseRenderer.TryGetStaticBatchClaim(out int[]? subMeshIndices))
			{
				continue;
			}

			if (renderer.GetMesh() is null)
			{
				continue;
			}

			claims.Add(new BatchClaim(renderer, subMeshIndices));
		}

		return claims;
	}

	private static bool IsWholeMesh(BatchClaim claim, MeshData source)
	{
		return claim.SubMeshIndices.Length == source.SubMeshes.Length && claim.SubMeshIndices[0] == 0;
	}

	private static bool TryValidate(BatchClaim claim, MeshData source, [NotNullWhen(false)] out string? error)
	{
		if (claim.SubMeshIndices.Length == 0)
		{
			error = "the batch claim is empty";
			return false;
		}

		if (source.SubMeshes.Length < claim.SubMeshIndices.Length)
		{
			error = $"the mesh has {source.SubMeshes.Length} sub-mesh(es) but the renderer claims {claim.SubMeshIndices.Length}";
			return false;
		}

		foreach (int index in claim.SubMeshIndices)
		{
			if ((uint)index >= (uint)source.SubMeshes.Length)
			{
				error = $"sub-mesh index {index} is out of range for a mesh with {source.SubMeshes.Length} sub-mesh(es)";
				return false;
			}

			SubMeshData subMesh = source.SubMeshes[index];
			if (subMesh.FirstIndex < 0 || subMesh.IndexCount < 0 || subMesh.FirstIndex + subMesh.IndexCount > source.ProcessedIndexBuffer.Length)
			{
				error = $"sub-mesh {index} has an index range outside of the index buffer";
				return false;
			}
		}

		error = null;
		return true;
	}

	/// <summary>
	/// Cuts the claimed sub-meshes out of <paramref name="source"/> into a new, standalone mesh asset.
	/// </summary>
	private static IMesh CreateSeparatedMesh(ProcessedAssetCollection collection, MeshData source, BatchClaim claim)
	{
		MeshData separated = Extract(source, claim.SubMeshIndices);

		IMesh mesh = collection.CreateMesh();
		mesh.Name = CleanMeshName(claim.Renderer.GetGameObjectName());
		mesh.FillWithCompressedMeshData(separated);

		return mesh;
	}

	/// <summary>
	/// Builds a self-contained <see cref="MeshData"/> containing only the given sub-meshes.
	/// </summary>
	/// <remarks>
	/// Static batching appends the source meshes into one shared vertex buffer, so the extracted
	/// sub-meshes still reference vertices scattered across that buffer. Every referenced vertex is
	/// therefore copied into a compact buffer and the indices are remapped to match.
	/// </remarks>
	private static MeshData Extract(MeshData source, int[] subMeshIndices)
	{
		Dictionary<uint, uint> remap = new();
		List<uint> indices = new(source.SubMeshes.Sum(static s => s.IndexCount));
		SubMeshData[] subMeshes = new SubMeshData[subMeshIndices.Length];

		int vertexCount = 0;

		for (int i = 0; i < subMeshIndices.Length; i++)
		{
			SubMeshData sourceSubMesh = source.SubMeshes[subMeshIndices[i]];
			int firstIndex = indices.Count;
			int firstVertex = vertexCount;

			for (int j = 0; j < sourceSubMesh.IndexCount; j++)
			{
				uint sourceVertex = source.ProcessedIndexBuffer[sourceSubMesh.FirstIndex + j];

				if (sourceVertex >= source.Vertices.Length)
				{
					throw new InvalidOperationException($"Index {sourceVertex} points past the end of a vertex buffer with {source.Vertices.Length} vertices.");
				}

				if (!remap.TryGetValue(sourceVertex, out uint target))
				{
					target = (uint)remap.Count;
					remap.Add(sourceVertex, target);
					vertexCount++;
				}

				indices.Add(target);
			}

			subMeshes[i] = new SubMeshData(
				BaseVertex: 0,
				FirstIndex: firstIndex,
				FirstVertex: firstVertex,
				IndexCount: sourceSubMesh.IndexCount,
				TriangleCount: sourceSubMesh.TriangleCount,
				VertexCount: vertexCount - firstVertex,
				Topology: sourceSubMesh.Topology,
				LocalBounds: sourceSubMesh.LocalBounds);
		}

		uint[] newIndexBuffer = indices.ToArray();
		int sourceVertexCount = source.Vertices.Length;

		return new MeshData(
			Vertices: RemapRequired(source.Vertices, remap, vertexCount),
			Normals: RemapChannel(source.Normals, sourceVertexCount, remap, vertexCount),
			Tangents: RemapChannel(source.Tangents, sourceVertexCount, remap, vertexCount),
			Colors: RemapChannel(source.Colors, sourceVertexCount, remap, vertexCount),
			UV0: RemapChannel(source.UV0, sourceVertexCount, remap, vertexCount),
			UV1: RemapChannel(source.UV1, sourceVertexCount, remap, vertexCount),
			UV2: RemapChannel(source.UV2, sourceVertexCount, remap, vertexCount),
			UV3: RemapChannel(source.UV3, sourceVertexCount, remap, vertexCount),
			UV4: RemapChannel(source.UV4, sourceVertexCount, remap, vertexCount),
			UV5: RemapChannel(source.UV5, sourceVertexCount, remap, vertexCount),
			UV6: RemapChannel(source.UV6, sourceVertexCount, remap, vertexCount),
			UV7: RemapChannel(source.UV7, sourceVertexCount, remap, vertexCount),
			Skin: RemapChannel(source.Skin, sourceVertexCount, remap, vertexCount),
			BindPose: source.BindPose,
			ProcessedIndexBuffer: newIndexBuffer,
			SubMeshes: subMeshes);
	}

	private static T[] RemapRequired<T>(T[] source, Dictionary<uint, uint> remap, int count)
	{
		T[] result = new T[count];
		foreach ((uint from, uint to) in remap)
		{
			result[to] = source[from];
		}
		return result;
	}

	/// <summary>
	/// Remaps an optional per-vertex channel. A channel whose length does not match the source
	/// vertex buffer carries no usable data for these vertices, which is the same rule
	/// <see cref="MeshData"/>'s <c>HasXxx</c> properties apply, so it is dropped.
	/// </summary>
	private static T[]? RemapChannel<T>(T[]? source, int sourceVertexCount, Dictionary<uint, uint> remap, int count)
	{
		return source is { Length: > 0 } && source.Length == sourceVertexCount
			? RemapRequired(source, remap, count)
			: null;
	}

	/// <summary>
	/// Mesh names do not survive static batching, so the GameObject name is used instead. Unity's
	/// instance suffixes and any path separators are removed to keep the result usable as a name.
	/// </summary>
	private static string CleanMeshName(string? gameObjectName)
	{
		if (string.IsNullOrWhiteSpace(gameObjectName))
		{
			return "Separated Mesh";
		}

		string name = gameObjectName.Trim();

		// Strip a trailing " (1)" style instance suffix.
		int suffixStart = name.LastIndexOf(" (", StringComparison.Ordinal);
		if (suffixStart > 0 && name.EndsWith(')'))
		{
			string inner = name[(suffixStart + 2)..^1];
			if (inner.Length > 0 && inner.All(char.IsAsciiDigit))
			{
				name = name[..suffixStart].TrimEnd();
			}
		}

		foreach (char invalid in Path.GetInvalidFileNameChars())
		{
			name = name.Replace(invalid, '_');
		}

		return name.Length == 0 ? "Separated Mesh" : name;
	}
}
