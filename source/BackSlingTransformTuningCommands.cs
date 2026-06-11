using System.Globalization;
using System.Text;
using CombatOverhaul.Armor;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace QuiversAndSheaths;

public static class BackSlingTransformTuningCommands
{
    private static readonly Dictionary<string, ModelTransform> OriginalTransforms = [];
    private static string? selectedCode;
    private static bool editStoredItemTransform;

    public static void Register(ICoreClientAPI api)
    {
        api.ChatCommands
            .GetOrCreate("qsoverlay")
            .WithDescription("Live-tune Quivers stored item overlay transforms")
            .WithArgs(api.ChatCommands.Parsers.OptionalAll("args"))
            .HandleWith(args => Handle(api, args));
    }

    private static TextCommandResult Handle(ICoreClientAPI api, TextCommandCallingArgs args)
    {
        string raw = args[0] as string ?? string.Empty;
        string[] parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string command = parts.Length > 0 ? parts[0].ToLowerInvariant() : "status";

        try
        {
            return command switch
            {
                "help" => Success(Help()),
                "base" => SetEditMode(storedItem: false),
                "item" => SetEditMode(storedItem: true),
                "list" => Success(ListTargets(api)),
                "select" => SelectTarget(api, parts),
                "status" => WithTarget(api, target => Success(TargetStatus(target))),
                "set" => WithTarget(api, target => SetTranslation(target, parts)),
                "nudge" => WithTarget(api, target => NudgeTranslation(target, parts)),
                "rot" => WithTarget(api, target => SetRotation(target, parts)),
                "rotnudge" => WithTarget(api, target => NudgeRotation(target, parts)),
                "scale" => WithTarget(api, target => SetScale(target, parts)),
                "reset" => WithTarget(api, Reset),
                "copy" => WithTarget(api, target => CopyTransform(api, target)),
                _ => Success($"{Help()}\nUnknown qsoverlay command: {command}")
            };
        }
        catch (Exception exception)
        {
            return TextCommandResult.Error(exception.Message, string.Empty);
        }
    }

    private static TextCommandResult SetEditMode(bool storedItem)
    {
        editStoredItemTransform = storedItem;
        return Success(storedItem
            ? "Editing stored item transform overrides. Put the tool you want to tune in the holster first."
            : "Editing base overlay transform.");
    }

    private static TextCommandResult SelectTarget(ICoreClientAPI api, string[] parts)
    {
        if (parts.Length < 2) return Success(ListTargets(api));

        List<OverlayTarget> targets = FindTargets(api);
        if (targets.Count == 0) return TextCommandResult.Error("No equipped Quivers overlay item found.", string.Empty);

        string selector = parts[1];
        OverlayTarget? selected = null;

        if (int.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) &&
            index >= 0 &&
            index < targets.Count)
        {
            selected = targets[index];
        }
        else
        {
            selected = targets.FirstOrDefault(target =>
                target.Code.Contains(selector, StringComparison.OrdinalIgnoreCase));
        }

        if (selected == null)
        {
            return TextCommandResult.Error($"No equipped Quivers overlay item matches '{selector}'.", string.Empty);
        }

        selectedCode = selected.Code;
        CaptureOriginal(selected);
        return Success($"Selected {selected.Code}\n{FormatTransform(selected.Transform)}");
    }

    private static TextCommandResult WithTarget(ICoreClientAPI api, System.Func<OverlayTarget, TextCommandResult> action)
    {
        OverlayTarget? target = ResolveTarget(api);
        if (target == null)
        {
            return TextCommandResult.Error("No equipped Quivers overlay item found. Use .qsoverlay list after equipping one.", string.Empty);
        }

        CaptureOriginal(target);
        return action(target);
    }

    private static OverlayTarget? ResolveTarget(ICoreClientAPI api)
    {
        List<OverlayTarget> targets = FindTargets(api);
        if (targets.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(selectedCode))
        {
            OverlayTarget? selected = targets.FirstOrDefault(target => target.Code == selectedCode);
            if (selected != null) return selected;
        }

        return targets.FirstOrDefault(target => target.Code.Contains("holster-tools", StringComparison.OrdinalIgnoreCase)) ??
               targets.FirstOrDefault(target => target.Code.Contains("sling-polearms", StringComparison.OrdinalIgnoreCase)) ??
               targets[0];
    }

    private static List<OverlayTarget> FindTargets(ICoreClientAPI api)
    {
        List<OverlayTarget> targets = [];
        InventoryBase? gearInventory = api.World.Player?.Entity?.GetBehavior<EntityBehaviorPlayerInventory>()?.Inventory;
        if (gearInventory == null) return targets;

        for (int index = 0; index < gearInventory.Count; index++)
        {
            ItemSlot slot = gearInventory[index];
            if (slot is not GearSlot) continue;

            ItemStack? stack = slot.Itemstack;
            BackSlingRenderConfigBehavior? behavior = stack?.Collectible?.GetBehavior<BackSlingRenderConfigBehavior>();
            if (stack?.Collectible?.Code == null || behavior == null) continue;

            ItemStack? storedStack = GetStoredStack(api, stack, behavior.Config);
            targets.Add(BuildTarget(index, stack.Collectible.Code.ToString(), behavior.Config, storedStack));
        }

        return targets;
    }

    private static OverlayTarget BuildTarget(int slotIndex, string code, BackSlingStoredWeaponRenderConfig config, ItemStack? storedStack)
    {
        if (editStoredItemTransform && storedStack?.Collectible?.Code != null)
        {
            string storedCode = storedStack.Collectible.Code.ToString();
            BackSlingStoredWeaponItemTransform itemTransform = ResolveOrCreateStoredItemTransform(config, storedCode, out string pattern);
            return new OverlayTarget(slotIndex, code, itemTransform.Transform, "stored item", pattern, storedCode);
        }

        return new OverlayTarget(slotIndex, code, config.Transform, "base", null, storedStack?.Collectible?.Code?.ToString());
    }

    private static ItemStack? GetStoredStack(ICoreClientAPI api, ItemStack slingStack, BackSlingStoredWeaponRenderConfig config)
    {
        ITreeAttribute? backpackTree = slingStack.Attributes.GetTreeAttribute("backpack");
        ITreeAttribute? slotsTree = backpackTree?.GetTreeAttribute("slots");
        if (slotsTree == null) return null;

        string preferredSlotKey = $"slot-{Math.Max(0, config.SlotIndex)}";
        if (TryResolveStoredStack(api, slotsTree[preferredSlotKey]?.GetValue() as ItemStack, out ItemStack? preferred))
        {
            return preferred;
        }

        foreach ((_, IAttribute attribute) in slotsTree.SortedCopy())
        {
            if (TryResolveStoredStack(api, attribute?.GetValue() as ItemStack, out ItemStack? storedStack))
            {
                return storedStack;
            }
        }

        return null;
    }

    private static bool TryResolveStoredStack(ICoreClientAPI api, ItemStack? stack, out ItemStack? resolved)
    {
        resolved = null;
        if (stack == null || stack.StackSize <= 0) return false;

        stack.ResolveBlockOrItem(api.World);
        if (stack.Collectible == null) return false;

        resolved = stack;
        return true;
    }

    private static BackSlingStoredWeaponItemTransform ResolveOrCreateStoredItemTransform(
        BackSlingStoredWeaponRenderConfig config,
        string storedCode,
        out string pattern)
    {
        config.TransformByStoredItem ??= [];

        BackSlingStoredWeaponItemTransform? bestTransform = null;
        string? bestPattern = null;
        int bestSpecificity = -1;

        foreach ((string existingPattern, BackSlingStoredWeaponItemTransform transform) in config.TransformByStoredItem)
        {
            if (!MatchesWildcard(existingPattern, storedCode)) continue;

            int specificity = existingPattern.Count(character => character != '*');
            if (specificity <= bestSpecificity) continue;

            bestPattern = existingPattern;
            bestTransform = transform;
            bestSpecificity = specificity;
        }

        if (bestTransform != null && bestPattern != null)
        {
            pattern = bestPattern;
            return bestTransform;
        }

        pattern = storedCode;
        BackSlingStoredWeaponItemTransform created = new();
        config.TransformByStoredItem[pattern] = created;
        return created;
    }

    private static bool MatchesWildcard(string pattern, string value)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        if (pattern == "*") return true;

        string[] parts = pattern.Split('*');
        int index = 0;

        for (int partIndex = 0; partIndex < parts.Length; partIndex++)
        {
            string part = parts[partIndex];
            if (part.Length == 0) continue;

            int found = value.IndexOf(part, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return false;
            if (partIndex == 0 && !pattern.StartsWith('*') && found != 0) return false;

            index = found + part.Length;
        }

        return pattern.EndsWith('*') || index == value.Length;
    }

    private static string ListTargets(ICoreClientAPI api)
    {
        List<OverlayTarget> targets = FindTargets(api);
        if (targets.Count == 0) return "No equipped Quivers overlay item found.";

        StringBuilder builder = new();
        builder.AppendLine("Quivers overlay targets:");
        for (int index = 0; index < targets.Count; index++)
        {
            OverlayTarget target = targets[index];
            string marker = target.Code == selectedCode ? " *" : string.Empty;
            builder.AppendLine($"{index}: slot {target.SlotIndex} {target.Code}{marker}");
            if (!string.IsNullOrWhiteSpace(target.StoredCode)) builder.AppendLine($"   stored: {target.StoredCode}");
            builder.AppendLine($"   editing: {target.EditMode}{(target.Pattern == null ? string.Empty : $" ({target.Pattern})")}");
            builder.AppendLine($"   {FormatTransform(target.Transform)}");
        }

        return builder.ToString().TrimEnd();
    }

    private static TextCommandResult SetTranslation(OverlayTarget target, string[] parts)
    {
        if (!TryReadVec(parts, 1, out Vec3f values, out string error)) return TextCommandResult.Error(error, string.Empty);

        target.Transform.Translation.X = values.X;
        target.Transform.Translation.Y = values.Y;
        target.Transform.Translation.Z = values.Z;
        return Success($"Set translation for {target.Code}\n{FormatTransform(target.Transform)}");
    }

    private static TextCommandResult NudgeTranslation(OverlayTarget target, string[] parts)
    {
        if (!TryReadVec(parts, 1, out Vec3f values, out string error)) return TextCommandResult.Error(error, string.Empty);

        target.Transform.Translation.X += values.X;
        target.Transform.Translation.Y += values.Y;
        target.Transform.Translation.Z += values.Z;
        return Success($"Nudged translation for {target.Code}\n{FormatTransform(target.Transform)}");
    }

    private static TextCommandResult SetRotation(OverlayTarget target, string[] parts)
    {
        if (!TryReadVec(parts, 1, out Vec3f values, out string error)) return TextCommandResult.Error(error, string.Empty);

        target.Transform.Rotation.X = values.X;
        target.Transform.Rotation.Y = values.Y;
        target.Transform.Rotation.Z = values.Z;
        return Success($"Set rotation for {target.Code}\n{FormatTransform(target.Transform)}");
    }

    private static TextCommandResult NudgeRotation(OverlayTarget target, string[] parts)
    {
        if (!TryReadVec(parts, 1, out Vec3f values, out string error)) return TextCommandResult.Error(error, string.Empty);

        target.Transform.Rotation.X += values.X;
        target.Transform.Rotation.Y += values.Y;
        target.Transform.Rotation.Z += values.Z;
        return Success($"Nudged rotation for {target.Code}\n{FormatTransform(target.Transform)}");
    }

    private static TextCommandResult SetScale(OverlayTarget target, string[] parts)
    {
        if (parts.Length < 2 || !TryReadFloat(parts[1], out float scale))
        {
            return TextCommandResult.Error("Usage: .qsoverlay scale <value>", string.Empty);
        }

        target.Transform.Scale = scale;
        return Success($"Set scale for {target.Code}\n{FormatTransform(target.Transform)}");
    }

    private static TextCommandResult Reset(OverlayTarget target)
    {
        string key = TargetKey(target);
        if (!OriginalTransforms.TryGetValue(key, out ModelTransform? original))
        {
            return TextCommandResult.Error($"No captured original transform for {target.Code}.", string.Empty);
        }

        CopyInto(original, target.Transform);
        return Success($"Reset {target.Code}\n{FormatTransform(target.Transform)}");
    }

    private static TextCommandResult CopyTransform(ICoreClientAPI api, OverlayTarget target)
    {
        string json = ToJsonSnippet(target.Transform);
        if (target.Pattern != null)
        {
            json = $"\"{target.Pattern}\": {{\n{Indent(json)}\n}}";
        }

        api.Input.ClipboardText = json;
        return Success($"Copied transform JSON for {target.Code} to clipboard:\n{json}");
    }

    private static string TargetStatus(OverlayTarget target)
    {
        string stored = string.IsNullOrWhiteSpace(target.StoredCode) ? string.Empty : $"\nstored: {target.StoredCode}";
        string pattern = string.IsNullOrWhiteSpace(target.Pattern) ? string.Empty : $"\npattern: {target.Pattern}";
        return $"{target.Code}{stored}\nediting: {target.EditMode}{pattern}\n{FormatTransform(target.Transform)}";
    }

    private static void CaptureOriginal(OverlayTarget target)
    {
        string key = TargetKey(target);
        if (!OriginalTransforms.ContainsKey(key))
        {
            OriginalTransforms[key] = Clone(target.Transform);
        }
    }

    private static string TargetKey(OverlayTarget target)
    {
        return $"{target.Code}|{target.EditMode}|{target.Pattern ?? "base"}";
    }

    private static bool TryReadVec(string[] parts, int startIndex, out Vec3f values, out string error)
    {
        values = new Vec3f();
        error = string.Empty;

        if (parts.Length < startIndex + 3)
        {
            error = "Expected 3 numbers: x y z";
            return false;
        }

        if (!TryReadFloat(parts[startIndex], out values.X) ||
            !TryReadFloat(parts[startIndex + 1], out values.Y) ||
            !TryReadFloat(parts[startIndex + 2], out values.Z))
        {
            error = "Could not parse x y z values.";
            return false;
        }

        return true;
    }

    private static bool TryReadFloat(string text, out float value)
    {
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static ModelTransform Clone(ModelTransform transform)
    {
        return new ModelTransform
        {
            Translation = new Vec3f(transform.Translation.X, transform.Translation.Y, transform.Translation.Z),
            Rotation = new Vec3f(transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z),
            Origin = new Vec3f(transform.Origin.X, transform.Origin.Y, transform.Origin.Z),
            Scale = transform.ScaleXYZ.X
        };
    }

    private static void CopyInto(ModelTransform source, ModelTransform target)
    {
        target.Translation.X = source.Translation.X;
        target.Translation.Y = source.Translation.Y;
        target.Translation.Z = source.Translation.Z;
        target.Rotation.X = source.Rotation.X;
        target.Rotation.Y = source.Rotation.Y;
        target.Rotation.Z = source.Rotation.Z;
        target.Origin.X = source.Origin.X;
        target.Origin.Y = source.Origin.Y;
        target.Origin.Z = source.Origin.Z;
        target.Scale = source.ScaleXYZ.X;
    }

    private static string FormatTransform(ModelTransform transform)
    {
        return $"translation {F(transform.Translation.X)} {F(transform.Translation.Y)} {F(transform.Translation.Z)}; " +
               $"rotation {F(transform.Rotation.X)} {F(transform.Rotation.Y)} {F(transform.Rotation.Z)}; " +
               $"scale {F(transform.ScaleXYZ.X)}";
    }

    private static string ToJsonSnippet(ModelTransform transform)
    {
        return "\"transform\": {\n" +
               "  \"translation\": {\n" +
               $"    \"x\": {F(transform.Translation.X)},\n" +
               $"    \"y\": {F(transform.Translation.Y)},\n" +
               $"    \"z\": {F(transform.Translation.Z)}\n" +
               "  },\n" +
               "  \"rotation\": {\n" +
               $"    \"x\": {F(transform.Rotation.X)},\n" +
               $"    \"y\": {F(transform.Rotation.Y)},\n" +
               $"    \"z\": {F(transform.Rotation.Z)}\n" +
               "  },\n" +
               "  \"origin\": {\n" +
               $"    \"x\": {F(transform.Origin.X)},\n" +
               $"    \"y\": {F(transform.Origin.Y)},\n" +
               $"    \"z\": {F(transform.Origin.Z)}\n" +
               "  },\n" +
               $"  \"scale\": {F(transform.ScaleXYZ.X)}\n" +
               "}";
    }

    private static string Indent(string text)
    {
        return string.Join("\n", text.Split('\n').Select(line => $"  {line}"));
    }

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Help()
    {
        return "Quivers overlay live tuning:\n" +
               ".qsoverlay base             edit shared/base transform\n" +
               ".qsoverlay item             edit transform override for the stored item\n" +
               ".qsoverlay list\n" +
               ".qsoverlay select <index|code-part>\n" +
               ".qsoverlay status\n" +
               ".qsoverlay set <x> <y> <z>\n" +
               ".qsoverlay nudge <dx> <dy> <dz>\n" +
               ".qsoverlay rot <x> <y> <z>\n" +
               ".qsoverlay rotnudge <dx> <dy> <dz>\n" +
               ".qsoverlay scale <value>\n" +
               ".qsoverlay reset\n" +
               ".qsoverlay copy";
    }

    private static TextCommandResult Success(string message)
    {
        return TextCommandResult.Success(message, null);
    }

    private sealed record OverlayTarget(
        int SlotIndex,
        string Code,
        ModelTransform Transform,
        string EditMode,
        string? Pattern,
        string? StoredCode);
}
