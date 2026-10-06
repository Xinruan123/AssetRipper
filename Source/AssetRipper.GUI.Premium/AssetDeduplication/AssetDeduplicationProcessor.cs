using AssetRipper.Assets;
using AssetRipper.Assets.Cloning;
using AssetRipper.Import.Logging;
using AssetRipper.Processing;
using AssetRipper.Processing.Textures;
using AssetRipper.SourceGenerated.Classes.ClassID_115;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Classes.ClassID_48;
using AssetRipper.SourceGenerated.Classes.ClassID_49;
using AssetRipper.SourceGenerated.Classes.ClassID_72;
using AssetRipper.SourceGenerated.Classes.ClassID_83;

namespace AssetRipper.GUI.Premium.AssetDeduplication;

/// <summary>
/// Reverses the asset duplication that Unity performs when it builds multiple asset bundles.
/// </summary>
/// <remarks>
/// Unity keeps every bundle self contained, so an asset shared between two bundles is stored
/// twice. AssetRipper faithfully exports both copies, which leaves the exported project with
/// duplicate assets where the original project only had one.
/// <para>
/// This processor finds those copies by deep value comparison and registers a redirection from
/// every copy except one to that one. The redirections are consumed at export time by
/// <see cref="RedirectingAssetExporter"/>, so no asset reference is modified here. That keeps the
/// operation reversible and, more importantly, keeps it from breaking the asset graph.
/// </para>
/// <para>
/// The comparison itself is the official <see cref="AssetEqualityComparer"/>, which resolves
/// PPtr fields recursively, so two assets are only considered copies when their referenced
/// assets are copies as well.
/// </para>
/// </remarks>
public sealed class AssetDeduplicationProcessor : IAssetProcessor
{
	/// <summary>
	/// The asset types that this processor deduplicates.
	/// </summary>
	/// <remarks>
	/// This is the set that the official documentation lists for the asset deduplication feature.
	/// Assets outside of it are deliberately left untouched because redirecting them could break
	/// the way they are exported. Materials, for example, are not deduplicated even though they
	/// are duplicated across bundles just as often.
	/// </remarks>
	public static IReadOnlyList<Type> SupportedAssetTypes { get; } =
	[
		typeof(IMonoScript),
		typeof(IShader),
		typeof(IComputeShader),
		typeof(IAudioClip),
		typeof(ITextAsset),
		typeof(IMesh),
		typeof(ITexture2D),
	];

	private const int LoggedExampleLimit = 10;

	private readonly Dictionary<IUnityObjectBase, IUnityObjectBase> redirections = new();
	private readonly HashSet<Type> incomparableTypes = [];

	/// <summary>
	/// Maps every duplicate asset to the asset that replaces it in the exported project.
	/// </summary>
	public IReadOnlyDictionary<IUnityObjectBase, IUnityObjectBase> Redirections => redirections;

	/// <summary>
	/// The number of assets that were considered for deduplication.
	/// </summary>
	public int CandidateCount { get; private set; }

	public bool HasRedirections => redirections.Count > 0;

	public void Process(GameData gameData)
	{
		redirections.Clear();
		CandidateCount = 0;

		Dictionary<GroupKey, List<IUnityObjectBase>> groups = CollectCandidates(gameData);

		AssetEqualityComparer comparer = new();
		List<IUnityObjectBase> representatives = [];
		List<string> examples = [];
		int affectedGroupCount = 0;

		foreach (KeyValuePair<GroupKey, List<IUnityObjectBase>> pair in groups)
		{
			List<IUnityObjectBase> group = pair.Value;
			if (group.Count < 2)
			{
				continue;
			}

			representatives.Clear();

			// The first asset of a group becomes the representative of its equivalence class and
			// every later asset is only compared against the representatives. Equality here is
			// transitive, so comparing against one member of a class is enough.
			foreach (IUnityObjectBase asset in group)
			{
				IUnityObjectBase? canonical = FindCanonical(comparer, representatives, asset);
				if (canonical is null)
				{
					representatives.Add(asset);
				}
				else if (redirections.TryAdd(asset, canonical))
				{
					if (examples.Count < LoggedExampleLimit)
					{
						examples.Add(Describe(asset, canonical));
					}
				}
			}

			if (representatives.Count < group.Count)
			{
				affectedGroupCount++;
			}
		}

		LogSummary(groups.Count, affectedGroupCount, examples);
	}

	private Dictionary<GroupKey, List<IUnityObjectBase>> CollectCandidates(GameData gameData)
	{
		HashSet<ITexture2D> texturesWithSprites = FindTexturesWithSprites(gameData);

		Dictionary<GroupKey, List<IUnityObjectBase>> groups = new();
		foreach (IUnityObjectBase asset in gameData.GameBundle.FetchAssets())
		{
			if (!IsSupported(asset, texturesWithSprites))
			{
				continue;
			}

			CandidateCount++;

			// Grouping by type and name is an optimization, not a correctness requirement: it
			// keeps the pairwise deep comparisons limited to assets that could plausibly match.
			// Two copies of the same asset always carry the same name because the name is part
			// of the serialized data that gets duplicated.
			GroupKey key = new(asset.GetType(), asset.GetBestName());
			if (!groups.TryGetValue(key, out List<IUnityObjectBase>? group))
			{
				group = [];
				groups.Add(key, group);
			}
			group.Add(asset);
		}

		return groups;
	}

	/// <summary>
	/// Textures that have sprites are exported as part of their <see cref="SpriteInformationObject"/>,
	/// so redirecting the texture on its own would break that grouping.
	/// </summary>
	private static HashSet<ITexture2D> FindTexturesWithSprites(GameData gameData)
	{
		HashSet<ITexture2D> result = [];
		foreach (IUnityObjectBase asset in gameData.GameBundle.FetchAssets())
		{
			if (asset is SpriteInformationObject spriteInformation)
			{
				result.Add(spriteInformation.Texture);
			}
		}
		return result;
	}

	private static bool IsSupported(IUnityObjectBase asset, HashSet<ITexture2D> texturesWithSprites)
	{
		if (asset.MainAsset is { } mainAsset && !ReferenceEquals(mainAsset, asset))
		{
			// Assets that belong to a main asset are exported as a unit with it. This covers
			// font textures, terrain splat maps and sprite atlases.
			return false;
		}

		return asset switch
		{
			IMonoScript or IShader or IComputeShader or IAudioClip or ITextAsset or IMesh => true,
			ITexture2D texture => !texturesWithSprites.Contains(texture),
			_ => false,
		};
	}

	private IUnityObjectBase? FindCanonical(AssetEqualityComparer comparer, List<IUnityObjectBase> representatives, IUnityObjectBase asset)
	{
		foreach (IUnityObjectBase representative in representatives)
		{
			if (AssetsAreIdentical(comparer, asset, representative))
			{
				return representative;
			}
		}
		return null;
	}

	private bool AssetsAreIdentical(AssetEqualityComparer comparer, IUnityObjectBase x, IUnityObjectBase y)
	{
		try
		{
			return comparer.Equals(x, y);
		}
		catch (Exception exception)
		{
			// A failed comparison must never break the import. Keeping the assets separate is
			// always a safe fallback, so that is what happens here.
			if (incomparableTypes.Add(x.GetType()))
			{
				Logger.Warning(LogCategory.Processing, $"Asset deduplication: {x.ClassName} assets could not be compared and will be kept separate. {exception.Message}");
			}
			return false;
		}
	}

	private static string Describe(IUnityObjectBase duplicate, IUnityObjectBase canonical)
	{
		return $"  removed duplicate {duplicate.ClassName} '{duplicate.GetBestName()}' from collection '{duplicate.Collection.Name}' in favor of '{canonical.Collection.Name}'";
	}

	private void LogSummary(int groupCount, int affectedGroupCount, List<string> examples)
	{
		Logger.Info(LogCategory.Processing, $"Asset deduplication: examined {CandidateCount} candidate asset(s) in {groupCount} name group(s); merged {redirections.Count} duplicate(s) in {affectedGroupCount} group(s).");

		foreach (string example in examples)
		{
			Logger.Info(LogCategory.Processing, example);
		}

		if (redirections.Count > examples.Count)
		{
			Logger.Info(LogCategory.Processing, $"  ... and {redirections.Count - examples.Count} more");
		}
	}

	private readonly record struct GroupKey(Type AssetType, string Name);
}
