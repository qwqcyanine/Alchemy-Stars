namespace AlchemyStars.Avalonia;

/// <summary>--qc-smoke &lt;models-dir&gt; &lt;anims-dir&gt; &lt;locomotion-dir|-&gt; &lt;output-dir&gt;: classifies every LOD0 CAST under models-dir and generates a QC project.</summary>
internal static class QcSmoke
{
    public static int Run(string[] args)
    {
        try
        {
            if (args.Length < 4) throw new ArgumentException("usage: --qc-smoke <models> <anims> <locomotion|-> <output> [both|mwbase|arc9]");
            Check();
            var printName = CheckUi(args[0], args[1], args[3]);
            var parts = Directory.GetFiles(args[0], "*_LOD0.cast", SearchOption.AllDirectories)
                .Select(path => new ModelPartSpec(path, ModelPartClassifier.Classify(path).Kind))
                .OrderBy(part => part.Kind).ThenBy(part => part.FilePath, StringComparer.OrdinalIgnoreCase).ToArray();
            var target = args.Length > 4 && !args[4].StartsWith("--", StringComparison.Ordinal) ? Enum.Parse<QcTarget>(args[4], true) : QcTarget.Both;
            var result = QcProjectGenerator.Generate(new(parts, args[1], args[2] == "-" ? "" : args[2], args[3], Target: target, Scale: new(),
                ConvertMaterials: args.Contains("--materials"), IncludeHands: args.Contains("--hands"), PrintName: printName));
            foreach (var line in result.MaterialLog ?? []) Console.WriteLine("material: " + line);
            Console.WriteLine(result.ViewModelQc);
            Console.WriteLine(result.WorldModelQc);
            Console.WriteLine("sequences: " + string.Join(' ', result.Sequences));
            Console.WriteLine("layers: " + string.Join(' ', result.Layers));
            Console.WriteLine("missing: " + string.Join(' ', result.MissingLayers));
            foreach (var qc in result.AttachmentQcs ?? []) Console.WriteLine("attachment: " + qc);
            if (result.LuaDirectory is not null) Console.WriteLine("lua: " + result.LuaDirectory);
            return result.Sequences.Contains("idle") ? 0 : 1;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    /// <summary>Drag-drop routing and project save/load round-trip of the QC page inputs.</summary>
    private static string CheckUi(string models, string animations, string output)
    {
        void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException("QC UI: " + message); }
        var casts = Directory.GetFiles(models, "*_LOD0.cast", SearchOption.AllDirectories);
        var hands = casts.Single(path => ModelPartClassifier.Classify(path).Kind == ModelPartKind.ViewHands);
        var directory = Path.Combine(Path.GetTempPath(), "AlchemyStarsQcUi", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new WorkspaceProjectStore();
            var preferences = new ApplicationPreferencesStore(Path.Combine(directory, "settings.json"));
            using var vm = new MainWindowViewModel(new AnimationExportEngine(), store, preferences, new NullPicker());
            vm.DropQcPaths(casts, null);
            Require(vm.QcHandsFile == hands && File.Exists(vm.QcWeaponFile), "auto-detect missed hands or receiver");
            Require(vm.QcAttachments.Count == casts.Length - 2, $"expected {casts.Length - 2} attachments, got {vm.QcAttachments.Count}");
            vm.FillQcPrintNameAsync().GetAwaiter().GetResult();
            var printName = vm.QcPrintName;
            Console.WriteLine($"print name: {Path.GetFileName(vm.QcWeaponFile)} -> '{printName}'");
            vm.QcPrintName = "Custom Name";
            vm.FillQcPrintNameAsync().GetAwaiter().GetResult();
            Require(vm.QcPrintName == "Custom Name", "auto-fill overwrote a typed display name");
            var clip = Directory.GetFiles(animations, "*.cast")[0];
            vm.DropQcPaths([clip], null);
            Require(vm.QcAnimationsFolder == Path.GetDirectoryName(clip), "animation clip did not set the animations folder");
            vm.DropQcPaths([clip], "loco");
            Require(vm.QcLocomotionFolder == Path.GetDirectoryName(clip), "field drop did not fill locomotion");
            Directory.CreateDirectory(output);
            vm.DropQcPaths([output], "output");
            Require(vm.QcOutputFolder == output, "folder drop did not fill output");
            vm.QcTargetIndex = 2;
            vm.QcModelName = "weapons/test/gun";
            var project = Path.Combine(directory, "qc.alchemyproj");
            Require(vm.Workspace.Qc.HandsFile.Length == 0, "Workspace.Qc must only sync on save");
            vm.SaveQcToWorkspace();
            store.Save(vm.Workspace, project);
            vm.NewProject();
            Require(vm.QcHandsFile.Length == 0 && vm.QcAttachments.Count == 0, "new project kept QC inputs");
            vm.LoadProject(project);
            Require(vm.QcHandsFile == hands && vm.QcAttachments.Count == casts.Length - 2 && vm.QcOutputFolder == output
                && vm.QcTargetIndex == 2 && vm.QcModelName == "weapons/test/gun" && vm.QcPrintName == "Custom Name", "project round-trip lost QC inputs");
            return printName;
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class NullPicker : IWorkspaceFilePicker
    {
        public Task<IReadOnlyList<string>> PickFilesAsync(FilePickerPurpose purpose, bool allowMultiple) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string?> PickProjectDestinationAsync(string? currentPath) => Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync(string? currentPath) => Task.FromResult<string?>(null);
        public Task<bool> OpenUriAsync(Uri uri) => Task.FromResult(false);
    }

    private static void Check()
    {
        void Is(string? actual, string? expected) { if (actual != expected) throw new InvalidOperationException($"QC mapping: '{actual}' != '{expected}'"); }
        Is(QcProjectGenerator.SequenceName("raise_first"), "draw_first");
        Is(QcProjectGenerator.SequenceName("drop"), "holster");
        Is(QcProjectGenerator.SequenceName("selectsemi_on"), "semi_on");
        Is(QcProjectGenerator.SequenceName("ads_up"), "ads_in");
        Is(QcProjectGenerator.SequenceName("ads_down_settle"), null);
        Is(QcProjectGenerator.SequenceName("branched_empty_reload_01"), null);
        Is(QcProjectGenerator.SequenceName("pose_l"), null);
        var seen = false;
        Is(QcProjectGenerator.EventLine("ps_wfoly_plr_x_reload_01", 3, "reload", ref seen), "{ event 5004 3 \"wfoly_plr_x_reload_01\" }");
        Is(QcProjectGenerator.EventLine("ik_out_start_left_hand", 0, "reload", ref seen), "{ event 9011 0 \"0\" }");
        Is(QcProjectGenerator.EventLine("ik_in_start_right_hand", 9, "reload", ref seen), "{ event 9021 9 \"1\" }");
        Is(QcProjectGenerator.EventLine("reload checkpoint", 30, "reload", ref seen), "{ event 9031 30 \"ResetBullets\" }");
        Is(QcProjectGenerator.EventLine("reload checkpoint", 40, "reload", ref seen), null);
        Is(QcProjectGenerator.Token("vm_jup_jp03_sm_pmike_mag_30_9p_000", "vm_jup_jp03_sm_pmike_rec_000"), "mag_30_9p");
        Is(QcProjectGenerator.Token("vm_jup_jp03_sm_pmike_rec_000", "vm_jup_jp03_sm_pmike_rec_000"), "rec");
    }
}
