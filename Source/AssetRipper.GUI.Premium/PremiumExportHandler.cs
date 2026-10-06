using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.GUI.Premium.AssetDeduplication;
using AssetRipper.GUI.Premium.StaticMeshSeparation;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.Processing;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.ProcessingLayers;

namespace AssetRipper.GUI.Premium;

/// <summary>
/// The export handler used by the premium edition.
/// </summary>
/// <remarks>
/// The GUI decides which edition it is at runtime by comparing the type of the installed
/// handler against <see cref="ExportHandler"/>:
/// <code>GameFileLoader.Premium =&gt; ExportHandler.GetType() != typeof(ExportHandler)</code>
/// Installing this subclass is therefore all that is needed to turn every premium-only
/// setting in the GUI back on.
/// </remarks>
internal sealed class PremiumExportHandler : ExportHandler
{
	static PremiumExportHandler()
	{
		EnableIl2CppRecovery();
	}

	public PremiumExportHandler(FullConfiguration settings) : base(settings)
	{
	}

	private readonly AssetDeduplicationProcessor assetDeduplicationProcessor = new();

	/// <summary>
	/// Supplies the processing layers and output format that script content level 3 needs.
	/// </summary>
	/// <remarks>
	/// <see cref="IL2CppManager"/> already contains the entire branch for level 3
	/// (<c>contentLevel == ScriptContentLevel.Level3</c>); the two static members it reads are
	/// left null in the free edition, which is what makes them fall back to the default layers.
	/// Assigning them here is what actually turns the feature on.
	/// <para>
	/// Note that the default layer list also contains <c>MethodOverrideNameFixer</c>, which is
	/// internal to Cpp2IL.Core and therefore cannot be reused here. Only publicly visible layers
	/// are available to this project.
	/// </para>
	/// </remarks>
	private static void EnableIl2CppRecovery()
	{
		IL2CppManager.RecoveryProcessingLayers =
		[
			new AttributeAnalysisProcessingLayer(),
			new CallAnalysisProcessingLayer(),
		];
		IL2CppManager.RecoveryOutputFormat = new AsmResolverDllOutputFormatIlRecovery();
	}

	protected override void BeforeExport(ProjectExporter projectExporter)
	{
		base.BeforeExport(projectExporter);

		FallBackFromUnimplementedShaderDecompilation();
		RegisterAssetDeduplication(projectExporter);
	}

	/// <summary>
	/// Teaches the project exporter that duplicate assets are to be redirected to their canonical
	/// copies instead of being written out again.
	/// </summary>
	private void RegisterAssetDeduplication(ProjectExporter projectExporter)
	{
		if (!Settings.ProcessingSettings.EnableAssetDeduplication)
		{
			return;
		}

		if (!assetDeduplicationProcessor.HasRedirections)
		{
			Logger.Info(LogCategory.Export, "Asset deduplication is enabled, but no duplicate assets were found.");
			return;
		}

		RedirectingAssetExporter exporter = new(assetDeduplicationProcessor.Redirections);
		foreach (Type assetType in AssetDeduplicationProcessor.SupportedAssetTypes)
		{
			projectExporter.OverrideExporter(assetType, exporter, allowInheritance: true);
		}

		Logger.Info(LogCategory.Export, $"Asset deduplication redirected {exporter.Count} duplicate asset(s) to their canonical copies.");
	}

	/// <summary>
	/// Shader decompilation is not implemented in this build, so selecting it would silently
	/// produce dummy shaders. Redirect it to the YAML exporter instead and say so in the log.
	/// </summary>
	private void FallBackFromUnimplementedShaderDecompilation()
	{
		if (Settings.ExportSettings.ShaderExportMode == ShaderExportMode.Decompile)
		{
			Logger.Warning(LogCategory.Export, "Shader decompilation is not implemented in this build. Falling back to YAML shader export.");
			Settings.ExportSettings.ShaderExportMode = ShaderExportMode.Yaml;
		}
	}

	/// <summary>
	/// Addressed premium processors to the base pipeline.
	/// </summary>
	protected override IEnumerable<IAssetProcessor> GetProcessors()
	{
		LogCapabilities();

		foreach (IAssetProcessor processor in base.GetProcessors())
		{
			// The base handler leaves a marker at exactly this point in its processor list:
			//     //Static mesh separation goes here
			//     yield return new LightingDataProcessor();//Needs to be after static mesh separation
			// so the processor is injected immediately before lighting data is processed.
			if (processor is LightingDataProcessor && Settings.ProcessingSettings.EnableStaticMeshSeparation)
			{
				yield return new StaticMeshSeparationProcessor();
			}

			yield return processor;
		}

		if (Settings.ProcessingSettings.EnableAssetDeduplication)
		{
			// Deduplication runs last so that it also sees the assets created by the processors
			// above, and so that the texture/sprite grouping done by SpriteProcessor is already
			// in place. Both matter for deciding which assets may be redirected.
			yield return assetDeduplicationProcessor;
		}
	}

	private static bool capabilitiesLogged;

	/// <summary>
	/// Logs which premium features are active. This runs from the processor pipeline rather than
	/// during construction because <see cref="Logger"/> silently drops messages written before
	/// <c>WebApplicationLauncher</c> has registered its loggers.
	/// </summary>
	private static void LogCapabilities()
	{
		if (capabilitiesLogged)
		{
			return;
		}

		capabilitiesLogged = true;

		string layers = IL2CppManager.RecoveryProcessingLayers is { } recoveryLayers
			? string.Join(", ", recoveryLayers.Select(static layer => layer.Name))
			: "none";
		string outputFormat = IL2CppManager.RecoveryOutputFormat?.OutputFormatId ?? "none";

		Logger.Info(LogCategory.General, $"Il2Cpp recovery (script content level 3) is configured with processing layers [{layers}] and output format '{outputFormat}'.");
	}
}
