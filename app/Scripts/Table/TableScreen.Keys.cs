using System.Text.Json;
using Godot;
using Gathering;

namespace ArkhamUI;

// Keys (The Innsmouth Conspiracy's coloured keys, Edge of the Earth's chaos tokens used as keys): the core lists them on the
// entity that holds them ("keys" on a location, an investigator, an enemy, an asset). QQ #29: none were drawn, neither on the
// investigators nor on the places. Drawn as the html version does (Key.vue): the key's own picture as a small rounded tile
// with a shadow; a face-down key shows the key back; a key the player can pick now (KeyLabel) is lit and is its button.
public partial class TableScreen
{
    static readonly System.Collections.Generic.Dictionary<string, string> KeyPictures = new()
    {
        ["BlueKey"] = "blue-key", ["GreenKey"] = "green-key", ["RedKey"] = "red-key", ["YellowKey"] = "yellow-key",
        ["PurpleKey"] = "purple-key", ["BlackKey"] = "black-key", ["WhiteKey"] = "white-key", ["UnrevealedKey"] = "key-back",
    };

    static System.Collections.Generic.List<JsonElement> KeysOf(JsonElement raw) => J.Items(J.Get(raw, "keys")).ToList();

    static Texture2D? KeyPicture(JsonElement key)
    {
        string tag = J.Tag(key);
        if (tag == "TokenKey")
        {
            var tok = J.Get(key, "contents");
            string face = J.String(tok, "chaosTokenFace"); if (face.Length == 0) face = J.String(tok, "face");
            return Ui.Token(face);
        }
        return KeyPictures.TryGetValue(tag, out var f) ? Ui.Tex("res://assets/tokens/keys/" + f + ".jpg") : null;
    }

    static string KeyTip(JsonElement key)
    {
        if (J.Tag(key) == "TokenKey")
        {
            var tok = J.Get(key, "contents");
            string face = J.String(tok, "chaosTokenFace"); if (face.Length == 0) face = J.String(tok, "face");
            return TokenName(face) + "标记（作为钥匙）";
        }
        return QuestionPresenter.KeyName(key);
    }

    static string KeyText(JsonElement key) => System.Text.RegularExpressions.Regex.Replace(key.GetRawText(), @"\s", "");

    // the offered choice that picks this key, if any
    ChoicePresentation? KeyChoice(JsonElement key)
    {
        if (model is null) return null;
        string want = KeyText(key);
        return model.Question.Choices.FirstOrDefault(c => c.Enabled && c.Tag == "KeyLabel" && KeyText(J.Get(c.Raw, "key")) == want);
    }

    // One key: its picture on a rounded tile with a soft shadow (html .key: 4 px corners, a drop shadow). Lit and clickable
    // when the core offers to pick it.
    Control KeyChip(JsonElement key, float size, string owner = "", ChoicePresentation? take = null)
    {
        var pick = take ?? KeyChoice(key);
        bool token = J.Tag(key) == "TokenKey";
        var chip = new Panel { CustomMinimumSize = new Vector2(size, size), Size = new Vector2(size, size), SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Stop, TooltipText = KeyTip(key) + (owner.Length > 0 ? "（" + owner + "）" : "") + (take is not null ? " · 点击拿取（免费）" : pick is not null ? " · 点击选择" : "") };
        var sb = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.35f), AntiAliasing = true, ShadowColor = pick is not null ? new Color(Style.Amber, 0.85f) : new Color(0, 0, 0, 0.55f),
            ShadowSize = pick is not null ? 10 : 4, ShadowOffset = pick is not null ? Vector2.Zero : new Vector2(1, 1) };
        sb.SetCornerRadiusAll(token ? (int)(size / 2) : Math.Max(3, (int)(size * 0.14f)));
        if (pick is not null) { sb.BorderColor = new Color("ffcf6e"); sb.SetBorderWidthAll(2); }
        chip.AddThemeStyleboxOverride("panel", sb);
        var clip = new Panel { MouseFilter = MouseFilterEnum.Ignore, ClipChildren = ClipChildrenMode.Only };
        var mask = new StyleBoxFlat { BgColor = Colors.White, AntiAliasing = true }; mask.SetCornerRadiusAll(token ? (int)(size / 2) : Math.Max(3, (int)(size * 0.14f)));
        clip.AddThemeStyleboxOverride("panel", mask);
        chip.AddChild(clip); clip.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var pic = new TextureRect { Texture = KeyPicture(key), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            MouseFilter = MouseFilterEnum.Ignore, TextureFilter = TextureFilterEnum.LinearWithMipmaps };
        clip.AddChild(pic); pic.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        chip.SetMeta("keyChip", KeyText(key));
        if (pick is not null)
        {
            chip.MouseDefaultCursorShape = CursorShape.PointingHand;
            var c = pick;
            chip.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) { chip.AcceptEvent(); Choose(c); } };
            if (S.Settings.ReducedMotion != true) { var tw = chip.CreateTween().SetLoops(); tw.TweenProperty(chip, "modulate", new Color(1.25f, 1.15f, 0.95f), 0.8f).SetTrans(Tween.TransitionType.Sine); tw.TweenProperty(chip, "modulate", Colors.White, 0.8f).SetTrans(Tween.TransitionType.Sine); }
        }
        return chip;
    }

    // The keys an entity holds as a row (or a column) of chips; null when it holds none.
    BoxContainer? KeyRow(JsonElement raw, float size, bool vertical = false, string owner = "", string locationId = "")
    {
        var keys = KeysOf(raw);
        if (keys.Count == 0) return null;
        BoxContainer box = vertical ? new VBoxContainer() : new HBoxContainer();
        box.MouseFilter = MouseFilterEnum.Ignore; box.AddThemeConstantOverride("separation", Math.Max(2, (int)(size * 0.12f)));
        for (int i = 0; i < keys.Count; i++) box.AddChild(KeyChip(keys[i], size, owner, locationId.Length > 0 ? TakeKeyChoice(locationId, i) : null));
        return box;
    }

    // A place's "take control of its key" ability (the core's ability 500 + the key's place on the location, offered once the
    // place has no clues): the key itself is its button (QQ #29)
    ChoicePresentation? TakeKeyChoice(string locationId, int index)
    {
        if (model is null) return null;
        return model.Question.Choices.FirstOrDefault(c => c.Enabled && c.Tag == "AbilityLabel" && J.Int(J.Get(c.Raw, "ability"), "index") == 500 + index
            && J.String(J.Get(J.Get(c.Raw, "ability"), "source"), "contents") == locationId);
    }

    // Keys on a small card (an enemy, an investigator's token on the map): a column inside its top-left corner.
    void AddKeyCorner(Control card, JsonElement raw, float size, string owner = "")
    {
        if (KeyRow(raw, size, vertical: true, owner) is not { } col) return;
        col.ZIndex = 3; card.AddChild(col); col.Position = new Vector2(-size * 0.25f, 2);
    }
}
