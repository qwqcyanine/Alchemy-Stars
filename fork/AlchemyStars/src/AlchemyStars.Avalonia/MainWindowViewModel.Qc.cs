using System.Collections.ObjectModel;

namespace AlchemyStars.Avalonia;

public sealed partial class MainWindowViewModel
{
    private string qcHandsFile = "", qcWeaponFile = "", qcAnimationsFolder = "", qcLocomotionFolder = "", qcOutputFolder = "";
    private string qcModelName = "", qcMaterialsPath = "", qcLog = "";
    private string? qcSelectedAttachment;
    public bool IsQcPage => SelectedPage == WorkspacePage.QcGenerator;
    // The model merger works on folders; the QC page saves its inputs in the project file.
    public bool ShowsProjectToolbar => !IsModelMergerPage;
    public string QcHandsFile { get => qcHandsFile; set { qcHandsFile = value ?? ""; OnPropertyChanged(); } }
    public string QcWeaponFile { get => qcWeaponFile; set { qcWeaponFile = value ?? ""; OnPropertyChanged(); } }
    public ObservableCollection<string> QcAttachments { get; } = [];
    public string? QcSelectedAttachment { get => qcSelectedAttachment; set { qcSelectedAttachment = value; OnPropertyChanged(); } }
    public string QcAnimationsFolder { get => qcAnimationsFolder; set { qcAnimationsFolder = value ?? ""; OnPropertyChanged(); } }
    public string QcLocomotionFolder { get => qcLocomotionFolder; set { qcLocomotionFolder = value ?? ""; OnPropertyChanged(); } }
    public string QcOutputFolder { get => qcOutputFolder; set { qcOutputFolder = value ?? ""; OnPropertyChanged(); } }
    public string QcModelName { get => qcModelName; set { qcModelName = value ?? ""; OnPropertyChanged(); } }
    public string QcMaterialsPath { get => qcMaterialsPath; set { qcMaterialsPath = value ?? ""; OnPropertyChanged(); } }
    public string QcLog { get => qcLog; private set { qcLog = value; OnPropertyChanged(); } }
    private int qcTargetIndex;
    public string[] QcTargetOptions => [Text.QcTargetBoth, "MW Base (Modern Wokefare)", "ARC9"];
    public int QcTargetIndex { get => qcTargetIndex; set { qcTargetIndex = Math.Clamp(value, 0, 2); OnPropertyChanged(); OnPropertyChanged(nameof(QcTargetHelp)); } }
    private double qcWalkScale = 1, qcJogScale = 1, qcSprintScale = 0.5, qcSuperSprintScale = 0.5;
    private bool qcConvertMaterials = true;
    private bool qcIncludeHands;
    public bool QcIncludeHands { get => qcIncludeHands; set { qcIncludeHands = value; OnPropertyChanged(); } }
    private int qcMaterialSizeIndex = 1;
    private static readonly int[] QcMaterialSizes = [1024, 2048, 4096];
    public bool QcConvertMaterials { get => qcConvertMaterials; set { qcConvertMaterials = value; OnPropertyChanged(); } }
    public string[] QcMaterialSizeOptions => ["1024", "2048", "4096"];
    public int QcMaterialSizeIndex { get => qcMaterialSizeIndex; set { qcMaterialSizeIndex = Math.Clamp(value, 0, 2); OnPropertyChanged(); } }
    public double QcWalkScale { get => qcWalkScale; set { qcWalkScale = Math.Clamp(value, 0, 1.5); OnPropertyChanged(); } }
    public double QcJogScale { get => qcJogScale; set { qcJogScale = Math.Clamp(value, 0, 1.5); OnPropertyChanged(); } }
    public double QcSprintScale { get => qcSprintScale; set { qcSprintScale = Math.Clamp(value, 0, 1.5); OnPropertyChanged(); } }
    public double QcSuperSprintScale { get => qcSuperSprintScale; set { qcSuperSprintScale = Math.Clamp(value, 0, 1.5); OnPropertyChanged(); } }
    public string QcTargetHelp => qcTargetIndex switch
    {
        1 => Text.QcTargetMwHelp,
        2 => Text.QcTargetArc9Help,
        _ => Text.QcTargetBothHelp,
    };

    public async Task PickQcAsync(string field)
    {
        if (field is "hands" or "weapon" or "attachments")
        {
            var files = await picker.PickFilesAsync(FilePickerPurpose.ModelPart, field == "attachments");
            if (files.Count == 0) return;
            if (field == "hands") QcHandsFile = files[0];
            else if (field == "weapon") QcWeaponFile = files[0];
            else AddQcAttachments(files);
            return;
        }
        var current = field switch { "anims" => QcAnimationsFolder, "loco" => QcLocomotionFolder, _ => QcOutputFolder };
        if (await picker.PickFolderAsync(current) is not { } path) return;
        if (field == "anims") QcAnimationsFolder = path;
        else if (field == "loco") QcLocomotionFolder = path;
        else QcOutputFolder = path;
    }

    public void RemoveQcAttachment()
    {
        if (QcSelectedAttachment is { } item) QcAttachments.Remove(item);
    }

    /// <summary>
    /// Fills QC inputs from dropped paths. A named field takes the drop directly (folder fields accept a
    /// folder or any file inside it). Without a field, model CASTs are classified (hands / receiver /
    /// attachment) and animation CASTs or folders set the animations folder.
    /// </summary>
    public void DropQcPaths(IReadOnlyList<string> paths, string? field)
    {
        static string Folder(string path) => Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
        var casts = paths.Where(path => File.Exists(path) && path.EndsWith(".cast", StringComparison.OrdinalIgnoreCase)).ToArray();
        switch (field)
        {
            case "hands" when casts.Length > 0: QcHandsFile = casts[0]; return;
            case "weapon" when casts.Length > 0: QcWeaponFile = casts[0]; return;
            case "attachments": AddQcAttachments(casts); return;
            case "anims" when paths.Count > 0: QcAnimationsFolder = Folder(paths[0]); return;
            case "loco" when paths.Count > 0: QcLocomotionFolder = Folder(paths[0]); return;
            case "output" when paths.Count > 0: QcOutputFolder = Folder(paths[0]); return;
        }
        var unrecognized = 0;
        foreach (var path in paths)
        {
            if (Directory.Exists(path)) { QcAnimationsFolder = path; continue; }
            if (!casts.Contains(path)) { unrecognized++; continue; }
            ModelPartClassification classification;
            try { classification = ModelPartClassifier.Classify(path); }
            catch (InvalidDataException) { QcAnimationsFolder = Folder(path); continue; } // no model node = animation clip
            catch (Exception) { unrecognized++; continue; }
            switch (classification.Kind)
            {
                case ModelPartKind.ViewHands: QcHandsFile = path; break;
                case ModelPartKind.Weapon when string.IsNullOrWhiteSpace(QcWeaponFile) || !File.Exists(QcWeaponFile): QcWeaponFile = path; break;
                default: AddQcAttachments([path]); break;
            }
        }
        if (unrecognized > 0) FooterStatus = Text.QcDropIgnored;
    }

    private void AddQcAttachments(IEnumerable<string> files)
    {
        foreach (var file in files.Where(file => !QcAttachments.Contains(file, StringComparer.OrdinalIgnoreCase)
            && !string.Equals(file, QcWeaponFile, StringComparison.OrdinalIgnoreCase)))
            QcAttachments.Add(file);
    }

    internal void SaveQcToWorkspace() => Workspace.Qc = new()
    {
        HandsFile = QcHandsFile, WeaponFile = QcWeaponFile, Attachments = [.. QcAttachments],
        AnimationsFolder = QcAnimationsFolder, LocomotionFolder = QcLocomotionFolder, OutputFolder = QcOutputFolder,
        ModelPath = QcModelName, MaterialsPath = QcMaterialsPath, Target = QcTargetIndex,
        WalkScale = (float)QcWalkScale, JogScale = (float)QcJogScale, SprintScale = (float)QcSprintScale, SuperSprintScale = (float)QcSuperSprintScale,
        ConvertMaterials = QcConvertMaterials, MaterialMaxSize = QcMaterialSizes[QcMaterialSizeIndex], IncludeHands = QcIncludeHands,
    };

    private void LoadQcFromWorkspace()
    {
        var qc = Workspace.Qc ?? new();
        QcHandsFile = qc.HandsFile; QcWeaponFile = qc.WeaponFile;
        QcAttachments.Clear();
        foreach (var file in qc.Attachments ?? []) QcAttachments.Add(file);
        QcAnimationsFolder = qc.AnimationsFolder; QcLocomotionFolder = qc.LocomotionFolder; QcOutputFolder = qc.OutputFolder;
        QcModelName = qc.ModelPath; QcMaterialsPath = qc.MaterialsPath; QcTargetIndex = qc.Target;
        QcWalkScale = qc.WalkScale; QcJogScale = qc.JogScale; QcSprintScale = qc.SprintScale; QcSuperSprintScale = qc.SuperSprintScale;
        QcConvertMaterials = qc.ConvertMaterials; QcIncludeHands = qc.IncludeHands; QcMaterialSizeIndex = Math.Max(0, Array.IndexOf(QcMaterialSizes, qc.MaterialMaxSize));
        QcLog = "";
    }

    public async Task GenerateQcAsync()
    {
        if (IsBusy) return;
        try
        {
            if (!File.Exists(QcHandsFile) || !File.Exists(QcWeaponFile) || !Directory.Exists(QcAnimationsFolder) || string.IsNullOrWhiteSpace(QcOutputFolder))
                throw new InvalidDataException(Text.QcMissingInput);
            IsBusy = true; BusyMessage = Text.QcGenerating;
            ModelPartSpec[] parts =
            [
                new(QcHandsFile, ModelPartKind.ViewHands),
                new(QcWeaponFile, ModelPartKind.Weapon),
                .. QcAttachments.Select(file => new ModelPartSpec(file, ModelPartKind.Attachment)),
            ];
            var request = new QcProjectRequest(parts, QcAnimationsFolder, QcLocomotionFolder, QcOutputFolder, QcModelName.Trim(), QcMaterialsPath.Trim(), (QcTarget)QcTargetIndex,
                new QcLocomotionScale((float)QcWalkScale, (float)QcJogScale, (float)QcSprintScale, (float)QcSuperSprintScale),
                QcConvertMaterials, QcMaterialSizes[QcMaterialSizeIndex], QcIncludeHands);
            var result = await Task.Run(() => QcProjectGenerator.Generate(request));
            QcLog = $"{result.ViewModelQc}\n{result.WorldModelQc}\n\n{Text.QcSequences} ({result.Sequences.Count}): {string.Join(", ", result.Sequences)}\n\n"
                + $"{Text.QcLayers} ({result.Layers.Count}): {string.Join(", ", result.Layers)}"
                + (result.MissingLayers.Count == 0 ? "" : $"\n\n{Text.QcMissingLayers}: {string.Join(", ", result.MissingLayers)}")
                + (result.AttachmentQcs is { Count: > 0 } atts ? $"\n\n{Text.QcAttachmentModels} ({atts.Count}):\n{string.Join("\n", atts)}" : "")
                + (result.LuaDirectory is { } lua ? $"\n\nLua: {lua}" : "")
                + (result.MaterialLog is { } mats ? $"\n\n{Text.QcMaterials} ({mats.Count(line => line.StartsWith("ok", StringComparison.Ordinal))}/{mats.Count}):\n{string.Join("\n", mats)}" : "");
            FooterStatus = Text.QcDone;
        }
        catch (Exception ex) { FooterStatus = Text.ExportFailed; ShowDialog(Text.QcNavigation, ex.Message, true); }
        finally { IsBusy = false; BusyMessage = ""; }
    }
}

public sealed partial class UiText
{
    public string QcMaterials => L("贴图（VMT/VTF）", "Materials (VMT/VTF)");
    public string QcIncludeHands => L("导出手臂网格（bodygroup \"hands\"，仅供 HLMV++ 预览手部动画；发布时关闭，游戏里用 c_hands）", "Export hands mesh (bodygroup \"hands\", for previewing hand animation in HLMV++; turn off for release, games use c_hands)");
    public string QcConvertMaterials => L("自动转换贴图：COD 的 _c&_s~ / NOG → Source phong 材质（_rgb / _n / _e），输出到 materials/<$cdmaterials>", "Convert textures: CoD _c&_s~ / NOG → Source phong materials (_rgb / _n / _e) in materials/<$cdmaterials>");
    public string QcMaterialSize => L("贴图最大尺寸", "Maximum texture size");
    public string QcDropHelp => L("可直接拖入 .cast 或文件夹：拖到某个字段上只填该字段；拖到其他位置会自动识别手臂 / 武器主体 / 附件，动画 .cast 或文件夹则设为动画目录。参数随工程保存（Ctrl+S）。", "Drop .cast files or folders: onto a field to fill only that field, or anywhere else to auto-detect hands / receiver / attachments; an animation .cast or folder sets the animations folder. Settings are saved with the project (Ctrl+S).");
    public string QcDropIgnored => L("部分拖入的文件不是可识别的 .cast，已忽略", "Some dropped files are not recognizable CAST files and were ignored");
    public string QcScaleHeader => L("共享 locomotion 幅度（仅影响从共享目录借用的 walk / jog / sprint）", "Shared locomotion amplitude (only clips borrowed from the shared folder)");
    public string QcTarget => L("目标武器 Base", "Target weapon base");
    public string QcTargetBoth => L("MW Base + ARC9（通用）", "MW Base + ARC9 (universal)");
    public string QcTargetBothHelp => L("同时写入 MW Base 的姿态参数叠加层与 ARC9 序列名（ready / enter_sights / idle_sprint 等），一个模型两个 Base 都能用。", "Writes MW Base pose-parameter layers and ARC9 sequence names (ready / enter_sights / idle_sprint…), so one model works on both bases.");
    public string QcTargetMwHelp => L("仅 MW Base：jog/walk/sprint/aim_offset 等 poseparameter 叠加层与 5004/9011/9021/9031 事件，不写 ARC9 别名序列。", "MW Base only: jog/walk/sprint/aim_offset pose-parameter layers and 5004/9011/9021/9031 events, no ARC9 alias sequences.");
    public string QcTargetArc9Help => L("仅 ARC9：QCA 1 枪口 / 2 抛壳 / 4 镜头，ARC9 命名序列；不写 MW Base 叠加层（ARC9 自己做程序化晃动）。", "ARC9 only: QCA 1 muzzle / 2 shell / 4 camera and ARC9-named sequences; no MW Base layers (ARC9 sways procedurally).");
    public string QcNavigation => L("QC 生成", "QC generator");
    public string QcHelp => L("从 CAST 模型与动画生成 Source 引擎视角模型 + 世界模型 QC 工程（含 SMD），兼容 MW Base 与 ARC9。手动选择手臂、武器主体与附件；同一挂点的多个附件会归入同一 bodygroup。", "Generate Source viewmodel + worldmodel QC projects with SMDs from CAST models and animations, compatible with MW Base and ARC9. Pick the view hands, receiver and attachments; attachments on the same mount share one bodygroup.");
    public string QcHandsFile => L("手臂 CAST", "View hands CAST");
    public string QcWeaponFile => L("武器主体 CAST（receiver）", "Receiver CAST");
    public string QcAttachments => L("附件 CAST", "Attachment CASTs");
    public string QcAddAttachments => L("添加附件", "Add attachments");
    public string QcRemoveAttachment => L("移除选中附件", "Remove selected attachment");
    public string QcAnimationsFolder => L("武器动画目录", "Weapon animations folder");
    public string QcLocomotionFolder => L("共享 locomotion 目录（可选）", "Shared locomotion folder (optional)");
    public string QcOutputFolder => L("输出目录", "Output folder");
    public string QcModelName => L("模型路径（生成 v_/w_，留空自动）", "Model path (writes v_/w_, blank = automatic)");
    public string QcMaterialsPath => L("$cdmaterials（留空自动）", "$cdmaterials (blank = automatic)");
    public string QcGenerate => L("生成 QC", "Generate QC");
    public string QcGenerating => L("正在生成 QC 与 SMD…", "Generating QC and SMDs…");
    public string QcDone => L("QC 工程已生成", "QC project generated");
    public string QcMissingInput => L("请选择手臂、武器主体、动画目录和输出目录。", "Choose the view hands, receiver, animations folder and output folder.");
    public string QcSequences => L("序列", "Sequences");
    public string QcLayers => L("叠加层", "Layers");
    public string QcMissingLayers => L("缺少源动画的层", "Layers without source clips");
    public string QcAttachmentModels => L("配件模型 QC（MW Base 独立 bonemerge）", "Attachment model QCs (MW Base bonemerge)");
}
