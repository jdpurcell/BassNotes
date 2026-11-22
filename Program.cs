using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace BassNotes;

static class Program {
	[STAThread]
	public static void Main(string[] args) =>
		AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
}

sealed class App : Application {
	public override void Initialize() {
		Name = "BassNotes";
		RequestedThemeVariant = ThemeVariant.Dark;
		Styles.Add(new FluentTheme());
	}

	public override void OnFrameworkInitializationCompleted() {
		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
			desktop.MainWindow = new MainWindow(desktop.Args?.FirstOrDefault());
		base.OnFrameworkInitializationCompleted();
	}
}
