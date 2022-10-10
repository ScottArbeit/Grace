namespace GraceApp;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
        System.Diagnostics.Debug.WriteLine($"CurrentDirectory: {System.Environment.CurrentDirectory}.");
		MainPage = new AppShell();
	}
}
