using AssetRipper.Import.Logging;
using AssetRipper.SourceGenerated.Classes.ClassID_23;
using AssetRipper.SourceGenerated.Classes.ClassID_25;
using AssetRipper.SourceGenerated.Classes.ClassID_33;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Subclasses.StaticBatchInfo;

namespace AssetRipper.GUI.Premium.StaticMeshSeparation;

/// <summary>
/// Helpers for reading and clearing the static batch information Unity leaves on a renderer.
/// </summary>
internal static class StaticBatchExtensions
{
	public static IMeshFilter? GetMeshFilter(this IRenderer renderer)
	{
		return renderer.GameObject_C25P?.TryGetComponent<IMeshFilter>();
	}

	public static IMesh? GetMesh(this IRenderer renderer)
	{
		return renderer.GetMeshFilter()?.MeshP;
	}

	public static string? GetGameObjectName(this IRenderer renderer)
	{
		return renderer.GameObject_C25P?.Name;
	}

	/// <summary>
	/// Gets the sub-mesh indices of the renderer's mesh that Unity assigned to this renderer while
	/// static batching.
	/// </summary>
	/// <remarks>
	/// Modern Unity versions store a contiguous range in <c>m_StaticBatchInfo</c>. Older versions
	/// stored an explicit list in <c>m_SubsetIndices</c> instead.
	/// </remarks>
	public static bool TryGetStaticBatchClaim(this IRenderer renderer, [NotNullWhen(true)] out int[]? subMeshIndices)
	{
		if (renderer.Has_StaticBatchInfo_C25())
		{
			IStaticBatchInfo staticBatchInfo = renderer.StaticBatchInfo_C25;
			if (!staticBatchInfo.IsDefault())
			{
				subMeshIndices = new int[staticBatchInfo.SubMeshCount];
				for (int i = 0; i < subMeshIndices.Length; i++)
				{
					subMeshIndices[i] = staticBatchInfo.FirstSubMesh + i;
				}
				return true;
			}
		}
		else if (renderer.Has_SubsetIndices_C25() && renderer.SubsetIndices_C25.Count != 0)
		{
			var subsetIndices = renderer.SubsetIndices_C25;
			subMeshIndices = new int[subsetIndices.Count];
			for (int i = 0; i < subsetIndices.Count; i++)
			{
				subMeshIndices[i] = checked((int)subsetIndices[i]);
			}
			return true;
		}

		subMeshIndices = null;
		return false;
	}

	/// <summary>
	/// Points the renderer's mesh filter at <paramref name="mesh"/> and clears the static batch
	/// information so the object is no longer treated as part of a batch.
	/// </summary>
	public static void AssignMesh(this IRenderer renderer, IMesh mesh)
	{
		IMeshFilter? meshFilter = renderer.GetMeshFilter();
		if (meshFilter is null)
		{
			throw new InvalidOperationException("The renderer has no MeshFilter to assign a mesh to.");
		}

		meshFilter.MeshP = mesh;
		ClearStaticBatchClaim(renderer);
	}

	public static void ClearStaticBatchClaim(this IRenderer renderer)
	{
		if (renderer.Has_StaticBatchInfo_C25())
		{
			renderer.StaticBatchInfo_C25.Initialize([]);
		}
		else if (renderer.Has_SubsetIndices_C25())
		{
			renderer.SubsetIndices_C25.Clear();
		}
		else
		{
			Logger.Verbose(LogCategory.Processing, "A renderer has no static batch information to clear.");
		}
	}
}
