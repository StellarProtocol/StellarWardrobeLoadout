using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.WardrobeLoadout;

/// <summary>
/// Overlay partial: the pooled OUTFITS rows (approved prototype). One row = a 26 px raised tile, 2 px apart:
/// <c>grip · badge · name · "N pcs" · Wear · rename · update · ✕</c>. States swap whole parts of the row
/// (rename → one input; delete / update confirm → a question + two small pills) so nothing else moves.
/// </summary>
public sealed partial class Plugin
{
    private const float GripCellW = 10f;
    private const float BadgeW = 30f;
    private const float BadgeH = 20f;
    private const float PiecesCellW = 74f;
    private const float WearW = 58f;
    private const float ConfirmPillW = 66f;
    private const float RenameInputW = 248f;   // = name (168) + gap (6) + pieces (74): the input replaces both cells

    private readonly ContrastCache _badgeText = new();
    private readonly ContrastCache _removeGlyph = new();
    private readonly ContrastCache _flashGood = new(), _flashBad = new();
    private string _confirmText = "";

    // The recycled row widgets. Each is built ONCE against its pool slot; the logical outfit it shows is resolved per
    // refresh through Idx().
    private HudElement[] BuildRowPool()
    {
        var pool = new HudElement[PoolRows];
        for (var i = 0; i < PoolRows; i++)
        {
            var slot = i;   // the POOL slot — fixed for the lifetime of the widget
            pool[slot] = BuildRow(() => _windowFirst + slot);
        }
        return pool;
    }

    // One pooled row. Idx() = the LOGICAL outfit index this widget renders on the current refresh — EVERY closure
    // below must go through it (or Item()), never a captured constant.
    private HudElement BuildRow(Func<int> Idx)
    {
        OutfitSlot? Item() => RowAt(Idx());
        var normal = new RowElement(new HudElement[]
        {
            new CellElement(new ConditionalElement(() => IsEditing(Idx()),
                Then: new RowElement(new HudElement[] { Grip(), Badge(Idx), RenameInput(Idx) }, Gap: 6f),
                Else: new SelectableElement(new RowElement(new HudElement[]
                    {
                        Grip(), Badge(Idx),
                        new CellElement(new TextElement(() => Item()?.Name ?? "", Color: () => TextColor(), NoWrap: true,
                            FontSize: 13) { Bold = true, Ellipsize = true }, Weight: 1f),
                        PiecesCell(Item),
                    }, Gap: 6f),
                    OnClick: () => { })   // the row itself only previews on hover; its buttons own the actions
                    { Bare = true, OnHover = over => OnRowHover(Idx(), over) }), Weight: 1f),
            WearCell(Idx, Item),
            RenameButton(Idx, Item),
            UpdateButton(Idx, Item),
            DeleteButton(Idx, Item),
        }, Gap: 6f);

        var confirm = new RowElement(new HudElement[]
        {
            new SpacerElement(GripCellW, 1f),   // the grip column stays, empty (no drag while confirming)
            Badge(Idx),
            new CellElement(new TextElement(() => _confirmText, Color: () => TextColor(), NoWrap: true, FontSize: 13)
                { Bold = true, Ellipsize = true }, Weight: 1f),
            new ConditionalElement(() => _confirmKind == ConfirmDelete,
                Then: DangerRowPill(() => _loc.T("wardrobe.button.delete"), ConfirmYes, ConfirmPillW, ConfirmGuardOpen, ConfirmRefused),
                Else: CautionRowPill(() => _loc.T("wardrobe.button.replace"), ConfirmYes, ConfirmPillW, ConfirmGuardOpen, ConfirmRefused)),
            NeutralRowPill(() => _loc.T("wardrobe.button.cancel"), CancelEdits, ConfirmPillW),
        }, Gap: 6f);

        return RowTile(Idx, Item, new ConditionalElement(() => IsConfirming(Idx()), Then: confirm, Else: normal));
    }

    // ── the tile ──────────────────────────────────────────────────────────────────────────────────────────────────
    // Outer bubble = the row fill + a 1 px ring; inner bubble (1 px in) = a second 1 px ring. Both rings on = the 2 px
    // accent ring of the previewed row (BubbleElement draws only 1 px outlines); only the outer one = the 1 px green /
    // red ring of a flash or a delete confirm. 24 px strut + 1 px padding each side = the 26 px tile, and the slot is
    // [tile, 2 px spacer] so the VirtualList (which stretches its child to the 28 px pitch) leaves a real gap (R15).
    private HudElement RowTile(Func<int> Idx, Func<OutfitSlot?> Item, HudElement content)
    {
        var inner = new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(3f, ListTileHeight - 2f),   // + the outer 1 px = the mockup's 4 px left inset
            new CellElement(content, Weight: 1f),
        }, Gap: 0.01f))
        {
            FillColorFn = () => Transparent,
            OutlineColorFn = () => RingPreviewed(Idx, Item) ? Fade(AccentColor()) : null,
            CornerRadius = TileRadius - 1f, PaddingX = 0f, PaddingY = 0f,
        };
        var tile = new BubbleElement(new ColumnElement(new HudElement[] { inner }, Gap: 0.01f))
        {
            FillColorFn = () => Fade(RowFill(Idx, Item)),
            OutlineColorFn = () => RowRing(Idx, Item) is { } c ? Fade(c) : null,
            CornerRadius = TileRadius, PaddingX = 1f, PaddingY = 1f,
        };
        return new ColumnElement(new HudElement[] { tile, new SpacerElement(0f, ListRowGap) }, Gap: 0.01f);
    }

    private bool RowAlarm(Func<int> Idx, Func<OutfitSlot?> Item)
        => (Flashing(Item()) && !_flashOk) || (IsConfirming(Idx()) && _confirmKind == ConfirmDelete);

    private ColorRgba RowFill(Func<int> Idx, Func<OutfitSlot?> Item)
        => Flashing(Item()) && _flashOk ? Over(SuccessColor() with { A = ChipWashAlpha }, TileColor())
         : RowAlarm(Idx, Item) ? Over(DangerColor() with { A = 0.20f }, TileColor())
         : TileColor();

    private ColorRgba? RowRing(Func<int> Idx, Func<OutfitSlot?> Item)
        => Flashing(Item()) && _flashOk ? SuccessColor()
         : RowAlarm(Idx, Item) ? DangerColor()
         : RingPreviewed(Idx, Item) ? AccentColor() : null;

    private bool RingPreviewed(Func<int> Idx, Func<OutfitSlot?> Item)
        => Item() is { } s && ReferenceEquals(s, _previewSlot) && !Flashing(s) && !IsConfirming(Idx());

    // ── cells ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // Drag grip (6 × 14 dots, centred in a 10 px column). Purely a cue: the whole row drags (VirtualList OnReorder).
    private HudElement Grip() => new CellElement(new RowElement(new HudElement[]
    {
        Glyph(GripIcon, () => TextColor() with { A = Busy ? 0.25f : 0.55f }, GripW),
    }, Gap: 0.01f, Justify: RowJustify.Center), Width: GripCellW);

    // 30 × 20 badge (fixed cell, R10): rows 1–8 = the slot hotkey (bound key or "H<n>") as an accent chip; rows 9+ =
    // the plain muted number, same cell (transparent chip).
    private HudElement Badge(Func<int> Idx) => new CellElement(new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, BadgeH),
            new TextElement(() => BadgeText(Idx()), Color: () => Idx() < HotkeySlotCount
                    ? _badgeText.Get(AccentColor(), BadgeFill(), PillMinContrast) : MutedColor(),
                Width: BadgeW, Align: TextAlign.Center, NoWrap: true, FontSize: 11)
                { Bold = true, Ellipsize = true, DynamicFontSize = () => BadgeFontSize(Idx()) },
        }, Gap: 0.01f, Justify: RowJustify.Center))
    {
        FillColorFn = () => Idx() < HotkeySlotCount ? Fade(BadgeFill()) : Transparent,
        CornerRadius = RoundRadius(BadgeH), PaddingX = 0f, PaddingY = 0f,
    }, Width: BadgeW);

    private ColorRgba BadgeFill() => Over(AccentColor() with { A = ChipWashAlpha }, TileColor());

    // "9 pcs"; during a flash the cell shows the bold "Saved" / "Not now" instead (bold can't toggle live → two complete
    // variants). No weapon-skin mark here: the small sword read as a ✓ tick in game (owner choice A, 2026-11-13) — the
    // preview line under the model still says "· weapon skin".
    private HudElement PiecesCell(Func<OutfitSlot?> Item) => new CellElement(new ConditionalElement(
        () => Flashing(Item()),
        Then: new TextElement(() => _flashText, Color: () => _flashOk
                ? _flashGood.Get(SuccessColor(), RowFillFlash(true), PillMinContrast)
                : _flashBad.Get(RemoveGlyphColor(), RowFillFlash(false), PillMinContrast),
            NoWrap: true, FontSize: 12) { Bold = true, Ellipsize = true },
        Else: new TextElement(() => Item() is { } s ? PiecesText(s) : "", Color: () => MutedColor(), NoWrap: true)),
        Width: PiecesCellW);

    private ColorRgba RowFillFlash(bool ok)
        => Over((ok ? SuccessColor() : DangerColor()) with { A = ok ? ChipWashAlpha : 0.20f }, TileColor());

    // The house remove-hover red (#ff8a8b on the dark presets) derived from the theme's Danger: Danger 35 % toward white.
    private ColorRgba RemoveGlyphColor() => Mix(DangerColor(), White, 0.35f);

    // Rename: name + pieces become ONE single-line input (same total width). FocusRequest takes keyboard focus the
    // moment the pencil is clicked; Enter submits (SingleLine: Enter commits and ends editing, never opens chat).
    // ⚠️ InputElement has a 28 px minimum height, so this row's tile is 30 px while renaming (overlaps the 2 px gap).
    private HudElement RenameInput(Func<int> Idx) => new InputElement(
        () => _editBuffer, _ => CommitRename(Idx()), Width: RenameInputW, OnChange: s => _editBuffer = s)
    {
        SingleLine = true,
        FocusRequest = () => _focusSeq,
    };

    // Wear (Primary 58 × 22) — or the green "Worn" chip when this outfit is what's worn. Recessed while any switch is in
    // flight (the switching row shows "…"), while renaming / confirming anywhere.
    private HudElement WearCell(Func<int> Idx, Func<OutfitSlot?> Item) => new CellElement(new ConditionalElement(
        () => Item() is { } s && ReferenceEquals(s, _wornSlot) && !ReferenceEquals(s, _switchingSlot) && !IsEditing(Idx()),
        Then: StatusRowChip(() => _loc.T("wardrobe.window.worn"), SuccessColor, WearW),
        Else: PrimaryRowPill(
            () => Item() is { } s && ReferenceEquals(s, _switchingSlot) ? "…" : _loc.T("wardrobe.window.wear"),
            () => OnWearRow(Idx()), WearW, () => CanWear(Idx()),
            over => { SetHint(HintWear, over); OnRowHover(Idx(), over); })), Width: WearW);

    // ── icon buttons ──────────────────────────────────────────────────────────────────────────────────────────────
    // Paused (another row is renaming / confirming): a dim glyph, no hover, no click.
    private ColorRgba PausedGlyph() => TextColor() with { A = 0.32f };

    // Pencil → ✓ while this row is renaming (same 24 px cell).
    private HudElement RenameButton(Func<int> Idx, Func<OutfitSlot?> Item)
    {
        var hover = new RowHover();
        return IconButton(() => IsEditing(Idx()) ? CheckIcon.Pick(_uiScale) : PencilIcon.Pick(_uiScale), PencilIcon.Units,
            Item, hover,
            tint: () => IsEditing(Idx()) || !Busy ? TextColor() : PausedGlyph(),
            wash: IconHoverWash,
            enabled: () => Item() != null && (IsEditing(Idx()) || !Busy),
            onClick: () => { if (IsEditing(Idx())) CommitRename(Idx()); else EnterEdit(Idx()); },
            onHover: over => { SetHint(IsEditing(Idx()) ? HintSaveName : HintRename, over, HintSaveName, HintRename);
                               OnRowHover(Idx(), over); });
    }

    // Update to what's worn now (arms the Replace confirm). Hidden (transparent, inert) on the row being renamed.
    private HudElement UpdateButton(Func<int> Idx, Func<OutfitSlot?> Item)
    {
        var hover = new RowHover();
        return IconButton(() => RefreshIcon.Pick(_uiScale), RefreshIcon.Units, Item, hover,
            tint: () => IsEditing(Idx()) ? Transparent : Busy ? PausedGlyph() : TextColor(),
            wash: IconHoverWash,
            enabled: () => Item() != null && !Busy,
            onClick: () =>
            {
                ArmConfirm(Idx(), ConfirmUpdate);
                if (IsConfirming(Idx())) _confirmText = _loc.T("wardrobe.confirm.replace");
            },
            onHover: over => { SetHint(HintUpdate, over); OnRowHover(Idx(), over); });
    }

    // ✕ = delete (arms the Delete confirm; red wash + red glyph on hover), or "cancel" on the row being renamed.
    private HudElement DeleteButton(Func<int> Idx, Func<OutfitSlot?> Item)
    {
        var hover = new RowHover();
        return IconButton(() => CloseIcon.Pick(_uiScale), CloseIcon.Units, Item, hover,
            tint: () => IsEditing(Idx()) ? (hover.Get(Item()) ? TextColor() : MutedColor())
                      : Busy ? PausedGlyph()
                      : hover.Get(Item()) ? _removeGlyph.Get(RemoveGlyphColor(), RemoveHoverWash(), 3f) : MutedColor(),
            wash: () => IsEditing(Idx()) ? IconHoverWash() : RemoveHoverWash(),
            enabled: () => Item() != null && (IsEditing(Idx()) || !Busy),
            onClick: () =>
            {
                if (IsEditing(Idx())) { CancelEdits(); return; }
                ArmConfirm(Idx(), ConfirmDelete);
                if (IsConfirming(Idx()) && Item() is { } s) _confirmText = _loc.TFormat("wardrobe.confirm.delete", s.Name);
            },
            onHover: over => { SetHint(IsEditing(Idx()) ? HintCancel : HintDelete, over, HintCancel, HintDelete);
                               OnRowHover(Idx(), over); });
    }
}
