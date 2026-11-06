namespace StarExam;

internal static class Program
{
    /// <summary>星星考題樂園 進入點。</summary>
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
