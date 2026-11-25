using MediaBrowser.Model.Plugins;
using System.Xml.Serialization;

namespace Jellyfin.Plugin.GlobalSubtitleTranslator;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public string MediaRoot { get; set; } = "/media";
    public string ManualScanDirectory { get; set; } = string.Empty;
    public List<string> ScanDirectories { get; set; } = [];
    public List<ScanRoutine> ScanRoutines { get; set; } = [];
    public List<SmartScanDirectoryBaseline> SmartScanBaselines { get; set; } = [];
    public DateTime? SmartScanLastCheckAtUtc { get; set; }
    public DateTime? LastScanAtUtc { get; set; }
    public string LastScanSummary { get; set; } = string.Empty;
    [XmlIgnore]
    public bool IsScanRunning { get; set; }
    [XmlIgnore]
    public DateTime? CurrentScanStartedAtUtc { get; set; }
    [XmlIgnore]
    public string CurrentRoutine { get; set; } = string.Empty;
    [XmlIgnore]
    public string CurrentFile { get; set; } = string.Empty;
    [XmlIgnore]
    public int CurrentFileIndex { get; set; }
    [XmlIgnore]
    public int CurrentFileCount { get; set; }
    [XmlIgnore]
    public string CurrentStage { get; set; } = string.Empty;
    [XmlIgnore]
    public int CurrentCueIndex { get; set; }
    [XmlIgnore]
    public int CurrentCueCount { get; set; }
    public string LastTranslatedFile { get; set; } = string.Empty;
    public DateTime? LastTranslatedAtUtc { get; set; }
    public string LastErrorFile { get; set; } = string.Empty;
    public string LastErrorMessage { get; set; } = string.Empty;
    public DateTime? LastErrorAtUtc { get; set; }
    public string GoogleCredentialsFile { get; set; } = "/config/plugins/global-subtitle-translator/google-translate.json";
    public string LocalWorkerQueueDirectory { get; set; } = "/config/local-gst-jobs";
    public int LocalWorkerTimeoutMinutes { get; set; } = 180;
    public bool GpuWorkerEnabled { get; set; }
    public string GpuWorkerIp { get; set; } = "";
    public bool IncludeParentheticalCaptions { get; set; }
    public List<string> ExcludedSubtitleLanguages { get; set; } = ["nb", "no"];
    public List<string> ExcludedFilenameKeywords { get; set; } = ["sample"];
    public int MinimumAcceptancePercent { get; set; } = 95;
    public bool TranslateOnPlaybackRequest { get; set; } = true;
    public bool TranslateNewFilesAutomatically { get; set; } = true;
    public bool ShowDebugInfo { get; set; }
    public List<TranslationProfile> Profiles { get; set; } = [new()];
}

public sealed class SmartScanDirectoryBaseline
{
    public string DirectoryPath { get; set; } = string.Empty;
    public int MkvCount { get; set; }
}

public sealed class ScanRoutine
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Filmer og serier";
    public List<string> Directories { get; set; } = [];
    public string Time { get; set; } = "03:00";
    public bool Enabled { get; set; } = true;
}

public sealed class TranslationProfile
{
    public string Name { get; set; } = "Norwegian Bokmål";
    public string SourceLanguage { get; set; } = "en";
    public string TargetLanguage { get; set; } = "nb";
    public bool Enabled { get; set; } = true;
}

