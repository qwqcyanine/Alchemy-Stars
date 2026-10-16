using System.Collections.ObjectModel;

namespace AlchemyStars.Avalonia;

public sealed partial class MainWindowViewModel
{
    private string qcHandsFile = "", qcWeaponFile = "", qcAnimationsFolder = "", qcLocomotionFolder = "", qcSoundsFolder = "", qcAmmoFile = "", qcOutputFolder = "";
    private string qcModelName = "", qcMaterialsPath = "", qcPrintName = "", qcAutoPrintName = "", qcLog = "";
    private string? qcSelectedAttachment;
    public bool IsQcPage => SelectedPage == WorkspacePage.QcGenerator;
    // The model merger works on folders; the QC page saves its inputs in the project file.
    public bool ShowsProjectToolbar => !IsModelMergerPage;
    public string QcHandsFile { get => qcHandsFile; set { qcHandsFile = value ?? ""; OnPropertyChanged(); } }
    public string QcWeaponFile { get => qcWeaponFile; set { qcWeaponFile = value ?? ""; OnPropertyChanged(); _ = FillQcPrintNameAsync(); } }
    public string QcPrintName { get => qcPrintName; set { qcPrintName = value ?? ""; OnPropertyChanged(); } }
    public ObservableCollection<string> QcAttachments { get; } = [];
    public string? QcSelectedAttachment { get => qcSelectedAttachment; set { qcSelectedAttachment = value; OnPropertyChanged(); } }
    public string QcAnimationsFolder { get => qcAnimationsFolder; set { qcAnimationsFolder = value ?? ""; OnPropertyChanged(); } }
    public string QcLocomotionFolder { get => qcLocomotionFolder; set { qcLocomotionFolder = value ?? ""; OnPropertyChanged(); } }
    public string QcAmmoFile { get => qcAmmoFile; set { qcAmmoFile = value ?? ""; OnPropertyChanged(); } }
    public string QcSoundsFolder { get => qcSoundsFolder; set { qcSoundsFolder = value ?? ""; OnPropertyChanged(); } }
    public string QcOutputFolder { get => qcOutputFolder; set { qcOutputFolder = value ?? ""; OnPropertyChanged(); } }
    public string QcModelName { get => qcModelName; set { qcModelName = value ?? ""; OnPropertyChanged(); } }
    public string QcMaterialsPath { get => qcMaterialsPath; set { qcMaterialsPath = value ?? ""; OnPropertyChanged(); } }
    public string QcLog { get => qcLog; private set { qcLog = value; OnPropertyChanged(); } }
    private int qcTargetIndex;
    public string[] QcTargetOptions => [Text.QcTargetBoth, "MW Base (Modern Wokefare)", "ARC9"];
    public int QcTargetIndex { get => qcTargetIndex; set { qcTargetIndex = Math.Clamp(value, 0, 2); OnPropertyChanged(); OnPropertyChanged(nameof(QcTargetHelp)); OnPropertyChanged(nameof(QcIsMwBase)); } }
    private int qcMwLayoutIndex;
    public bool QcIsMwBase => qcTargetIndex == 1;
    public string[] QcMwLayoutOptions => [Text.QcMwLayoutOfficial, Text.QcMwLayoutSingle];
    public int QcMwLayoutIndex { get => qcMwLayoutIndex; set { qcMwLayoutIndex = Math.Clamp(value, 0, 1); OnPropertyChanged(); } }
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
        if (field is "hands" or "weapon" or "attachments" or "ammo")
        {
            var files = await picker.PickFilesAsync(FilePickerPurpose.ModelPart, field == "attachments");
            if (files.Count == 0) return;
            if (field == "hands") QcHandsFile = files[0];
            else if (field == "weapon") SetQcWeapon(files[0]);
            else if (field == "ammo") QcAmmoFile = files[0];
            else AddQcAttachments(files);
            return;
        }
        var current = field switch { "anims" => QcAnimationsFolder, "loco" => QcLocomotionFolder, "sounds" => QcSoundsFolder, _ => QcOutputFolder };
        if (await picker.PickFolderAsync(current) is not { } path) return;
        if (field == "anims") QcAnimationsFolder = path;
        else if (field == "loco") QcLocomotionFolder = path;
        else if (field == "sounds") QcSoundsFolder = path;
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
            case "weapon" when casts.Length > 0: SetQcWeapon(casts[0]); return;
            case "ammo" when casts.Length > 0: QcAmmoFile = casts[0]; return;
            case "attachments": AddQcAttachments(casts); return;
            case "anims" when paths.Count > 0: QcAnimationsFolder = Folder(paths[0]); return;
            case "loco" when paths.Count > 0: QcLocomotionFolder = Folder(paths[0]); return;
            case "sounds" when paths.Count > 0: QcSoundsFolder = Folder(paths[0]); return;
            case "output" when paths.Count > 0: QcOutputFolder = Folder(paths[0]); return;
        }
        var unrecognized = 0;
        foreach (var path in paths)
        {
            if (Directory.Exists(path)) { QcAnimationsFolder = path; continue; }
            if (!casts.Contains(path)) { unrecognized++; continue; }
            if (QcAmmoFiller.IsAmmo(path)) { QcAmmoFile = path; continue; }
            ModelPartClassification classification;
            try { classification = ModelPartClassifier.Classify(path); }
            catch (InvalidDataException) { QcAnimationsFolder = Folder(path); continue; } // no model node = animation clip
            catch (Exception) { unrecognized++; continue; }
            switch (classification.Kind)
            {
                case ModelPartKind.ViewHands: QcHandsFile = path; break;
                case ModelPartKind.Weapon when string.IsNullOrWhiteSpace(QcWeaponFile) || !File.Exists(QcWeaponFile): SetQcWeapon(path); break;
                default: AddQcAttachments([path]); break;
            }
        }
        if (unrecognized > 0) FooterStatus = Text.QcDropIgnored;
    }

    /// <summary>
    /// Sets the receiver picked or dropped by the user and adds the same weapon's other exported parts
    /// (QcAttachments.Siblings: vm_jup_jp03_sm_pmike_* next to vm_jup_jp03_sm_pmike_rec_000). Removed parts stay removed
    /// until the receiver is set again. Project loading assigns QcWeaponFile directly, so saved lists are kept as is.
    /// </summary>
    private void SetQcWeapon(string path)
    {
        QcWeaponFile = path;
        IReadOnlyList<string> siblings;
        try { siblings = AlchemyStars.Engine.QcAttachments.Siblings(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return; }
        var before = QcAttachments.Count;
        AddQcAttachments(siblings);
        if (QcAttachments.Count > before)
            FooterStatus = string.Format(System.Globalization.CultureInfo.CurrentCulture, Text.QcAttachmentsFound, QcAttachments.Count - before);
    }

    private void AddQcAttachments(IEnumerable<string> files)
    {
        foreach (var file in files.Where(file => !QcAttachments.Contains(file, StringComparer.OrdinalIgnoreCase)
            && !string.Equals(file, QcWeaponFile, StringComparison.OrdinalIgnoreCase)))
            QcAttachments.Add(file);
        // Magazines ship without rounds; pick the matching ammo_unspent_<calibre> once, the user can change or clear it.
        if (string.IsNullOrWhiteSpace(QcAmmoFile) && QcAmmoFiller.Guess(QcAttachments) is { } ammo) QcAmmoFile = ammo;
    }

    /// <summary>
    /// Looks the receiver's codename (vm_jup_jp03_sm_pmike_rec_000 → sm_pmike) up in the COD weapon
    /// database and fills the display name. A name the user typed is kept; only a blank or previously
    /// auto-filled name is replaced.
    /// </summary>
    internal async Task FillQcPrintNameAsync()
    {
        var file = QcWeaponFile;
        if (string.IsNullOrWhiteSpace(file) || !await WaitForCodWeaponDbAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true)) return;
        if (file != QcWeaponFile || codCatalog?.MatchAsset(file) is not { } match) return;
        if (QcPrintName.Length > 0 && QcPrintName != qcAutoPrintName) return;
        QcPrintName = qcAutoPrintName = match.Name;
        FooterStatus = string.Format(System.Globalization.CultureInfo.CurrentCulture, Text.QcPrintNameMatched, match.Name, match.GameLabel, match.Codename);
    }

    internal void SaveQcToWorkspace() => Workspace.Qc = new()
    {
        HandsFile = QcHandsFile, WeaponFile = QcWeaponFile, Attachments = [.. QcAttachments],
        AnimationsFolder = QcAnimationsFolder, LocomotionFolder = QcLocomotionFolder, SoundsFolder = QcSoundsFolder, AmmoFile = QcAmmoFile, OutputFolder = QcOutputFolder,
        ModelPath = QcModelName, MaterialsPath = QcMaterialsPath, PrintName = QcPrintName, Target = QcTargetIndex, MwLayout = QcMwLayoutIndex,
        WalkScale = (float)QcWalkScale, JogScale = (float)QcJogScale, SprintScale = (float)QcSprintScale, SuperSprintScale = (float)QcSuperSprintScale,
        ConvertMaterials = QcConvertMaterials, MaterialMaxSize = QcMaterialSizes[QcMaterialSizeIndex], IncludeHands = QcIncludeHands,
    };

    private void LoadQcFromWorkspace()
    {
        var qc = Workspace.Qc ?? new();
        // Name first: setting the receiver auto-fills only a blank name, so a saved name survives the load.
        QcPrintName = qc.PrintName ?? ""; qcAutoPrintName = "";
        QcAmmoFile = qc.AmmoFile ?? "";
        QcHandsFile = qc.HandsFile; QcWeaponFile = qc.WeaponFile;
        QcAttachments.Clear();
        foreach (var file in qc.Attachments ?? []) QcAttachments.Add(file);
        QcAnimationsFolder = qc.AnimationsFolder; QcLocomotionFolder = qc.LocomotionFolder; QcSoundsFolder = qc.SoundsFolder ?? ""; QcOutputFolder = qc.OutputFolder;
        QcModelName = qc.ModelPath; QcMaterialsPath = qc.MaterialsPath; QcTargetIndex = qc.Target; QcMwLayoutIndex = qc.MwLayout;
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
            IReadOnlyList<ModelPartSpec> parts =
            [
                new(QcHandsFile, ModelPartKind.ViewHands),
                new(QcWeaponFile, ModelPartKind.Weapon),
                .. QcAttachments.Select(file => new ModelPartSpec(file, ModelPartKind.Attachment)),
            ];
            IReadOnlyList<string>? ammoLog = null;
            var ammoStaging = Path.Combine(Path.GetTempPath(), "AlchemyStarsQcAmmo", Guid.NewGuid().ToString("N"));
            if (!string.IsNullOrWhiteSpace(QcAmmoFile))
            {
                if (!File.Exists(QcAmmoFile)) throw new FileNotFoundException(Text.QcAmmoMissing, QcAmmoFile);
                BusyMessage = Text.QcFillingAmmo;
                (parts, ammoLog) = await QcAmmoFiller.FillAsync(new ModelMergerService(), parts, QcAmmoFile, ammoStaging);
                BusyMessage = Text.QcGenerating;
            }
            var request = new QcProjectRequest(parts, QcAnimationsFolder, QcLocomotionFolder, QcOutputFolder, QcModelName.Trim(), QcMaterialsPath.Trim(), (QcTarget)QcTargetIndex,
                new QcLocomotionScale((float)QcWalkScale, (float)QcJogScale, (float)QcSprintScale, (float)QcSuperSprintScale),
                QcConvertMaterials, QcMaterialSizes[QcMaterialSizeIndex], QcIncludeHands, QcPrintName.Trim(), QcSoundsFolder.Trim(), (QcMwLayout)QcMwLayoutIndex);
            QcProjectResult result;
            try { result = await Task.Run(() => QcProjectGenerator.Generate(request)); }
            finally { try { if (Directory.Exists(ammoStaging)) Directory.Delete(ammoStaging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            QcLog = $"{result.ViewModelQc}\n{result.WorldModelQc}\n\n{Text.QcSequences} ({result.Sequences.Count}): {string.Join(", ", result.Sequences)}\n\n"
                + $"{Text.QcLayers} ({result.Layers.Count}): {string.Join(", ", result.Layers)}"
                + (result.MissingLayers.Count == 0 ? "" : $"\n\n{Text.QcMissingLayers}: {string.Join(", ", result.MissingLayers)}")
                + (result.AttachmentQcs is { Count: > 0 } atts ? $"\n\n{Text.QcAttachmentModels} ({atts.Count}):\n{string.Join("\n", atts)}" : "")
                + (result.Notes is { Count: > 0 } notes ? "\n\n" + string.Join("\n", notes) : "")
                + (result.Sounds is { } sounds
                    ? $"\n\n{Text.QcSounds} ({sounds.Aliases.Count}/{sounds.Aliases.Count + sounds.Missing.Count})" + (sounds.SoundScript is { } script ? $": {script}" : "")
                        + (sounds.Missing.Count == 0 ? "" : $"\n{Text.QcMissingSounds} ({sounds.Missing.Count}):\n{string.Join("\n", sounds.Missing)}")
                    : "")
                + (ammoLog is null ? "" : $"\n\n{Text.QcAmmo} ({ammoLog.Count(line => line.StartsWith("ok", StringComparison.Ordinal))}):\n{string.Join("\n", ammoLog)}")
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
    public string QcMwLayout => L("MW Base 工程布局", "MW Base project layout");
    public string QcMwLayoutOfficial => L("官方（Source-Files-MW-Public：weapon_viewmodel.qc + 分文件，配件为独立 bonemerge 模型）", "Official (Source-Files-MW-Public: weapon_viewmodel.qc + includes, attachments as bonemerged models)");
    public string QcMwLayoutSingle => L("单文件（第三方 v_xxx.qc，配件为 bodygroup）", "Single file (third-party v_xxx.qc, attachments as bodygroups)");
    public string QcNavigation => L("QC 生成", "QC generator");
    public string QcHelp => L("从 CAST 模型与动画生成 Source 引擎视角模型 + 世界模型 QC 工程（含 SMD），兼容 MW Base 与 ARC9。手动选择手臂、武器主体与附件；同一挂点的多个附件会归入同一 bodygroup。", "Generate Source viewmodel + worldmodel QC projects with SMDs from CAST models and animations, compatible with MW Base and ARC9. Pick the view hands, receiver and attachments; attachments on the same mount share one bodygroup.");
    public string QcHandsFile => L("手臂 CAST", "View hands CAST");
    public string QcWeaponFile => L("武器主体 CAST（receiver）", "Receiver CAST");
    public string QcAttachments => L("附件 CAST", "Attachment CASTs");
    public string QcAddAttachments => L("添加附件", "Add attachments");
    public string QcRemoveAttachment => L("移除选中附件", "Remove selected attachment");
    public string QcAnimationsFolder => L("武器动画目录", "Weapon animations folder");
    public string QcLocomotionFolder => L("共享 locomotion 目录（可选）", "Shared locomotion folder (optional)");
    public string QcAmmoFile => L("子弹 CAST（可选；弹匣模型默认没有子弹，生成前用 ModelMerger 填入空的 j_ammo 槽位；添加弹匣附件时按口径自动选择 ammo_unspent_*）", "Round CAST (optional; magazine models ship without rounds, ModelMerger fills the empty j_ammo slots before generating; ammo_unspent_* is picked by calibre when a magazine is added)");
    public string QcAmmo => L("弹匣填弹", "Magazine rounds");
    public string QcAmmoMissing => L("找不到子弹 CAST", "Round CAST not found");
    public string QcFillingAmmo => L("正在为弹匣填入子弹…", "Filling magazines with rounds…");
    public string QcSoundsFolder => L("声音目录（可选，saluki 导出的 sounds；按动画 .cast 的 wfoly 音效事件自动复制）", "Sounds folder (optional, saluki-exported sounds; copied by the animation CASTs' wfoly sound events)");
    public string QcSounds => L("声音（已匹配别名 / 动画引用别名）", "Sounds (resolved aliases / aliases used by animations)");
    public string QcMissingSounds => L("缺失的声音", "Missing sounds");
    public string QcOutputFolder => L("输出目录", "Output folder");
    public string QcModelName => L("模型路径（生成 v_/w_，留空自动）", "Model path (writes v_/w_, blank = automatic)");
    public string QcPrintName => L("武器显示名（SWEP.PrintName，选择武器主体后按代号自动从 COD 武器库填入）", "Weapon display name (SWEP.PrintName, auto-filled from the COD weapon database by the receiver codename)");
    public string QcPrintNameMatched => L("COD 武器库匹配：{0}（{1} · {2}）", "COD weapon database match: {0} ({1} · {2})");
    public string QcAttachmentsFound => L("已自动添加同一武器的 {0} 个附件，可在列表中移除不需要的", "Added {0} attachments of the same weapon; remove any you do not want from the list");
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
