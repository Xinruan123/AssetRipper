using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Metadata;
using AssetRipper.Export.UnityProjects;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;

namespace AssetRipper.GUI.Premium.AssetDeduplication;

/// <summary>
/// An export collection for a duplicate asset, which writes no files of its own.
/// </summary>
/// <remarks>
/// Every reference to <see cref="Duplicate"/> is resolved to wherever <see cref="Canonical"/> is
/// exported. That happens lazily, when the export pointer is requested, because the GUID of the
/// export collection that owns the canonical asset is only assigned once that collection exists.
/// This mirrors how <c>SingleRedirectExportCollection</c> redirects engine assets, except that the
/// target here cannot be known up front.
/// </remarks>
public sealed class RedirectedAssetExportCollection : IExportCollection
{
	public RedirectedAssetExportCollection(IUnityObjectBase duplicate, IUnityObjectBase canonical)
	{
		Duplicate = duplicate ?? throw new ArgumentNullException(nameof(duplicate));
		Canonical = canonical ?? throw new ArgumentNullException(nameof(canonical));
	}

	/// <summary>
	/// The duplicate asset that this collection stands in for.
	/// </summary>
	public IUnityObjectBase Duplicate { get; }

	/// <summary>
	/// The asset that the duplicate is replaced with in the exported project.
	/// </summary>
	public IUnityObjectBase Canonical { get; }

	AssetCollection IExportCollection.File => Duplicate.Collection;

	TransferInstructionFlags IExportCollection.Flags => Duplicate.Collection.Flags;

	IEnumerable<IUnityObjectBase> IExportCollection.Assets => [Duplicate];

	bool IExportCollection.Exportable => false;

	string IExportCollection.Name => Duplicate.GetBestName();

	bool IExportCollection.Contains(IUnityObjectBase asset) => ReferenceEquals(Duplicate, asset);

	bool IExportCollection.Export(IExportContainer container, string projectDirectory, FileSystem fileSystem)
	{
		throw new NotSupportedException("A redirected asset does not export any files.");
	}

	MetaPtr IExportCollection.CreateExportPointer(IExportContainer container, IUnityObjectBase asset, bool isLocal)
	{
		ThrowIfNotDuplicate(asset);

		// Asking the container instead of building a MetaPtr here is what makes this work: the
		// container knows which collection owns the canonical asset and whether the reference
		// crosses file boundaries.
		return container.CreateExportPointer(Canonical);
	}

	long IExportCollection.GetExportID(IExportContainer container, IUnityObjectBase asset)
	{
		ThrowIfNotDuplicate(asset);
		return container.GetExportID(Canonical);
	}

	private void ThrowIfNotDuplicate(IUnityObjectBase asset)
	{
		if (!ReferenceEquals(Duplicate, asset))
		{
			throw new ArgumentException("The asset must be the duplicate that this collection redirects.", nameof(asset));
		}
	}
}
