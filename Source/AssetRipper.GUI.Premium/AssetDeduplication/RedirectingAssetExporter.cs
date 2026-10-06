using AssetRipper.Assets;
using AssetRipper.Export.UnityProjects;
using AssetRipper.IO.Files;

namespace AssetRipper.GUI.Premium.AssetDeduplication;

/// <summary>
/// An <see cref="IAssetExporter"/> that turns duplicate assets into redirections.
/// </summary>
/// <remarks>
/// This exporter only ever answers for assets that <see cref="AssetDeduplicationProcessor"/>
/// identified as duplicates. For everything else it returns <c>false</c>, which makes
/// <c>ProjectExporter</c> continue down its exporter stack to the regular exporter for that type.
/// As a result, this class never has to know how any asset is exported.
/// </remarks>
public sealed class RedirectingAssetExporter : IAssetExporter
{
	private readonly IReadOnlyDictionary<IUnityObjectBase, IUnityObjectBase> redirections;

	public RedirectingAssetExporter(IReadOnlyDictionary<IUnityObjectBase, IUnityObjectBase> redirections)
	{
		this.redirections = redirections ?? throw new ArgumentNullException(nameof(redirections));
	}

	/// <summary>
	/// The number of duplicate assets that this exporter redirects.
	/// </summary>
	public int Count => redirections.Count;

	public bool TryCreateCollection(IUnityObjectBase asset, [NotNullWhen(true)] out IExportCollection? exportCollection)
	{
		if (redirections.TryGetValue(asset, out IUnityObjectBase? canonical))
		{
			exportCollection = new RedirectedAssetExportCollection(asset, canonical);
			return true;
		}

		exportCollection = null;
		return false;
	}

	AssetType IAssetExporter.ToExportType(IUnityObjectBase asset) => AssetType.Serialized;

	bool IAssetExporter.ToUnknownExportType(Type type, out AssetType assetType)
	{
		// Returning false keeps the real exporter responsible for reporting the asset type, which
		// matters because the redirection delegates pointer creation to the canonical asset.
		assetType = default;
		return false;
	}
}
