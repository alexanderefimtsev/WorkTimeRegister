using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace WorkTimeRegister.Installer;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private const string ApplicationName = "WorkTimeRegister";
    private const string ApplicationExecutable = "WorkTimeRegister.exe";
    private const string PayloadResourceName = "WorkTimeRegister.Payload.zip";
    private const string UninstallRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WorkTimeRegister";
    private const uint MessageBoxOk = 0x00000000;
    private const uint MessageBoxYesNo = 0x00000004;
    private const uint MessageBoxIconError = 0x00000010;
    private const uint MessageBoxIconQuestion = 0x00000020;
    private const uint MessageBoxIconInformation = 0x00000040;
    private const uint MessageBoxTopMost = 0x00040000;
    private const int MessageBoxResultYes = 6;

    [STAThread]
    private static int Main()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 1;
        }

        try
        {
            return Install();
        }
        catch (Exception exception)
        {
            ShowMessage(
                $"Не удалось установить {ApplicationName}.\n\n{exception.Message}",
                "Ошибка установки",
                MessageBoxOk | MessageBoxIconError);
            return 1;
        }
    }

    private static int Install()
    {
        var version = GetApplicationVersion();
        var confirmation = ShowMessage(
            $"Установить или обновить {ApplicationName} до версии {version}?\n\n" +
            "Рабочие данные сохраняются отдельно и не будут удалены.",
            $"Установка {ApplicationName}",
            MessageBoxYesNo | MessageBoxIconQuestion);

        if (confirmation != MessageBoxResultYes)
        {
            return 0;
        }

        if (IsApplicationRunning())
        {
            ShowMessage(
                $"Закройте {ApplicationName} и повторите установку.",
                "Приложение запущено",
                MessageBoxOk | MessageBoxIconError);
            return 1;
        }

        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programsDirectory = Path.Combine(localApplicationData, "Programs");
        var installationDirectory = Path.Combine(programsDirectory, ApplicationName);
        var stagingDirectory = Path.Combine(programsDirectory, $".{ApplicationName}.update-{Guid.NewGuid():N}");
        var backupDirectory = Path.Combine(programsDirectory, $".{ApplicationName}.backup-{Guid.NewGuid():N}");
        var previousInstallationMoved = false;

        Directory.CreateDirectory(programsDirectory);

        try
        {
            ExtractPayload(stagingDirectory);
            var stagedExecutable = Path.Combine(stagingDirectory, ApplicationExecutable);

            if (!File.Exists(stagedExecutable))
            {
                throw new InvalidDataException($"В установочном пакете отсутствует {ApplicationExecutable}.");
            }

            WriteUninstallScript(stagingDirectory);

            if (Directory.Exists(installationDirectory))
            {
                Directory.Move(installationDirectory, backupDirectory);
                previousInstallationMoved = true;
            }

            Directory.Move(stagingDirectory, installationDirectory);
            CreateStartMenuShortcut(installationDirectory);
            RegisterUninstaller(installationDirectory, version);

            if (previousInstallationMoved)
            {
                TryDeleteDirectory(backupDirectory);
            }
        }
        catch
        {
            TryDeleteDirectory(stagingDirectory);

            if (previousInstallationMoved)
            {
                TryDeleteDirectory(installationDirectory);

                if (Directory.Exists(backupDirectory) && !Directory.Exists(installationDirectory))
                {
                    Directory.Move(backupDirectory, installationDirectory);
                }
            }

            throw;
        }

        var executablePath = Path.Combine(installationDirectory, ApplicationExecutable);
        var launch = ShowMessage(
            $"{ApplicationName} {version} установлен.\n\n" +
            "База данных сохранена в папке локальных данных пользователя.\n\n" +
            "Запустить приложение сейчас?",
            "Установка завершена",
            MessageBoxYesNo | MessageBoxIconInformation);

        if (launch == MessageBoxResultYes)
        {
            Process.Start(new ProcessStartInfo(executablePath) { UseShellExecute = true });
        }

        return 0;
    }

    private static void ExtractPayload(string destinationDirectory)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var payload = assembly.GetManifestResourceStream(PayloadResourceName)
            ?? throw new InvalidDataException("В установщике отсутствует архив приложения.");
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        archive.ExtractToDirectory(destinationDirectory);
    }

    private static bool IsApplicationRunning()
    {
        foreach (var process in Process.GetProcessesByName(ApplicationName))
        {
            using (process)
            {
                if (process.Id != Environment.ProcessId)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void WriteUninstallScript(string installationDirectory)
    {
        var scriptPath = Path.Combine(installationDirectory, "Uninstall.cmd");
        var script = """
            @echo off
            "%~dp0WorkTimeRegister.exe" --shutdown
            if errorlevel 1 (
                echo Не удалось штатно завершить WorkTimeRegister. Сохраните данные и закройте приложение, затем повторите удаление.
                pause
                exit /b 1
            )
            timeout /t 2 /nobreak >nul
            tasklist /FI "IMAGENAME eq WorkTimeRegister.exe" /NH | find /I "WorkTimeRegister.exe" >nul
            if not errorlevel 1 (
                echo WorkTimeRegister ещё запущен. Удаление отменено, база данных сохранена.
                pause
                exit /b 1
            )
            del /F /Q "%APPDATA%\Microsoft\Windows\Start Menu\Programs\WorkTimeRegister.lnk" >nul 2>&1
            reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\WorkTimeRegister" /F >nul 2>&1
            cd /D "%TEMP%"
            rmdir /S /Q "%LOCALAPPDATA%\Programs\WorkTimeRegister"
            """;

        File.WriteAllText(scriptPath, script, new UTF8Encoding(false));
    }

    private static void CreateStartMenuShortcut(string installationDirectory)
    {
        var executablePath = Path.Combine(installationDirectory, ApplicationExecutable);
        var shortcutPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            $"{ApplicationName}.lnk");
        var shellLinkObject = new ShellLink();
        var shellLink = (IShellLinkW)(object)shellLinkObject;

        try
        {
            shellLink.SetPath(executablePath);
            shellLink.SetWorkingDirectory(installationDirectory);
            shellLink.SetDescription("Учёт рабочего времени и перерывов");
            shellLink.SetIconLocation(executablePath, 0);
            ((IPersistFile)shellLink).Save(shortcutPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shellLinkObject);
        }
    }

    private static void RegisterUninstaller(string installationDirectory, string version)
    {
        var executablePath = Path.Combine(installationDirectory, ApplicationExecutable);
        var uninstallScript = Path.Combine(installationDirectory, "Uninstall.cmd");
        using var key = Registry.CurrentUser.CreateSubKey(UninstallRegistryPath, true)
            ?? throw new InvalidOperationException("Не удалось зарегистрировать приложение в Windows.");

        key.SetValue("DisplayName", ApplicationName);
        key.SetValue("DisplayVersion", version);
        key.SetValue("DisplayIcon", executablePath);
        key.SetValue("Publisher", ApplicationName);
        key.SetValue("InstallLocation", installationDirectory);
        key.SetValue("UninstallString", $"cmd.exe /d /c \"\"{uninstallScript}\"\"");
        key.SetValue("QuietUninstallString", $"cmd.exe /d /c \"\"{uninstallScript}\"\"");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", CalculateDirectorySizeInKilobytes(installationDirectory), RegistryValueKind.DWord);
    }

    private static int CalculateDirectorySizeInKilobytes(string directory)
    {
        var totalBytes = Directory
            .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length);
        return (int)Math.Min(int.MaxValue, Math.Max(1, totalBytes / 1024));
    }

    private static string GetApplicationVersion()
    {
        var informationalVersion = Assembly
            .GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return informationalVersion?.Split('+')[0] ?? "1.0.0";
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, true);
        }
        catch
        {
            // Cleanup failure must not replace the installation result.
        }
    }

    private static int ShowMessage(string message, string title, uint options) =>
        MessageBoxW(IntPtr.Zero, message, title, options | MessageBoxTopMost);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr windowHandle, string text, string caption, uint type);

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maximumPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr itemIdList);
        void SetIDList(IntPtr itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maximumName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maximumPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maximumPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCommand(out int showCommand);
        void SetShowCommand(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int iconPathLength, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr windowHandle, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
