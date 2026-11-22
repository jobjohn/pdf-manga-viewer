using System;
using System.IO;
using Microsoft.UI.Xaml;

namespace PdfMangaViewer;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        _window = window;
        window.Activate();

        // コマンドライン引数に PDF / ZIP / CBZ / 画像 / フォルダが渡されていればそのまま開く
        string? targetPath = FindTargetArgument();
        if (targetPath is not null)
        {
            window.ScheduleOpen(targetPath);
        }
    }

    private static string? FindTargetArgument()
    {
        string[] cmdArgs = Environment.GetCommandLineArgs();
        for (int i = 1; i < cmdArgs.Length; i++)
        {
            string arg = cmdArgs[i];
            if (MangaDocumentFactory.IsSupportedPath(arg))
            {
                return arg;
            }
        }

        return null;
    }
}
