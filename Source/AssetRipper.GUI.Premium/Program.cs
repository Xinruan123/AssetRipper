using AssetRipper.GUI.Web;

namespace AssetRipper.GUI.Premium;

internal static class Program
{
	public static void Main(string[] args)
	{
		// Installing the premium export handler is what switches the whole application over to
		// the premium edition: GameFileLoader.Premium compares the runtime type of the handler
		// against the base ExportHandler, and every premium-only setting in the GUI is gated on
		// that property.
		GameFileLoader.ExportHandler = new PremiumExportHandler(GameFileLoader.Settings);

		WebApplicationLauncher.Launch(args);
	}
}
