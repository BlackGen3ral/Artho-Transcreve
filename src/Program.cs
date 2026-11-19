namespace Artho.Transcreve;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args is ["--apply-update", _, _, _])
        {
            try { AppUpdate.RunInstaller(args); }
            catch (Exception error) { MessageBox.Show(error.Message, "Atualização do Artho Transcreve", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            return;
        }
        using var instance = new Mutex(true, @"Local\ArthoTranscreve-1", out var first);
        if (!first)
        {
            MessageBox.Show("O Artho Transcreve já está aberto.", "Artho Transcreve");
            return;
        }

        try
        {
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArthoTranscreve");
            var engine = new WhisperEngine(data);
            var app = new DesktopWindow(engine, PairingCode(data));
            Application.Run(app);
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "Artho Transcreve", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string PairingCode(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "pairing.txt");
        if (File.Exists(path))
        {
            var saved = File.ReadAllText(path).Trim();
            if (saved.Length == 32 && saved.All(Uri.IsHexDigit))
                return saved.ToUpperInvariant();
        }

        var code = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        File.WriteAllText(path, code);
        return code;
    }
}
