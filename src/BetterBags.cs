using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace BetterBags
{
    public class BagsConfig
    {
        public bool Hidden = false;
        public string Pos = "";          // "bag:x:y;bag:x:y" in gui units
        public bool Vertical = false;    // old setting, no longer used
        public string CraftPos = "";     // "x:y" of the crafting menu, empty = centre
        public int VertMask = 0;         // bit n set = bag n is laid out vertically (Height slider), otherwise horizontal (Width slider)
        public int PerRow = 8;           // slots per row (horizontal)
        public int PerColumn = 8;        // slots per column (vertical)
        public int SlotPct = 100;        // slot size in percent
        public int Transparency = 15;    // 0 = solid panel, 100 = invisible panel
        public int BorderStyle = 1;      // 0 off, 1 thin, 2 medium, 3 thick
        public int BorderColor = 0;
        public int BgColor = 0;
        public bool Rounded = true;
        public bool OnlyWithInventory = true; // bag rows only show while the inventory key's window (crafting grid) is open
    }

    // The game draws every slot from two shared size numbers. They are changed only for the moment one of our rows is composed, drawn or clicked, then put back.
    public static class SlotScale
    {
        private static bool init, ok;
        private static List<FieldInfo> fields = new List<FieldInfo>();
        private static List<double> orig = new List<double>();
        private static List<double> factor = new List<double>();
        private static int depth;

        private static void Init(ICoreClientAPI capi)
        {
            if (init) return;
            init = true;
            try
            {
                string[] types = { "Vintagestory.API.Client.GuiElementItemSlotGridBase", "Vintagestory.API.Client.GuiElementPassiveItemSlot" };
                string[] names = { "unscaledSlotSize", "unscaledItemSize" };
                BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                for (int i = 0; i < types.Length; i++)
                {
                    Type t = typeof(GuiComposer).Assembly.GetType(types[i]);
                    if (t == null) continue;
                    for (int j = 0; j < names.Length; j++)
                    {
                        FieldInfo f = t.GetField(names[j], bf);
                        if (f == null || f.IsLiteral || f.IsInitOnly) continue;
                        bool dup = false;
                        foreach (FieldInfo e in fields) if (e.Equals(f)) dup = true;
                        if (dup) continue;
                        fields.Add(f); orig.Add(Convert.ToDouble(f.GetValue(null))); factor.Add(1);
                    }
                }
                ok = fields.Count > 0;
                capi.Logger.Notification("[betterbags] slot size fields found: " + fields.Count);
            }
            catch (Exception e) { capi.Logger.Warning("[betterbags] slot size not available: " + e.Message); }
        }

        public static void Push(ICoreClientAPI capi, int pct)
        {
            Init(capi);
            depth++;
            if (!ok || pct == 100) return;
            try
            {
                for (int i = 0; i < fields.Count; i++)
                {
                    double v = orig[i] * pct / 100.0;
                    fields[i].SetValue(null, Convert.ChangeType(v, fields[i].FieldType));
                }
            }
            catch (Exception) { }
        }

        public static void Pop()
        {
            if (depth > 0) depth--;
            if (!ok) return;
            try
            {
                for (int i = 0; i < fields.Count; i++) fields[i].SetValue(null, Convert.ChangeType(orig[i], fields[i].FieldType));
            }
            catch (Exception) { }
        }
    }

    public class BetterBagsSystem : ModSystem
    {
        public ICoreClientAPI capi;
        public BagsConfig cfg = new BagsConfig();
        private CraftDialog craft;
        private BagsMenu menu;
        public List<int> ActiveBags = new List<int>();
        // The game numbers bags by where they sit in the backpack inventory. Combat Overhaul puts the character's
        // gear slots in front of them, so the 4 bags become 56-59 instead of 0-3. Positions, layout and the settings
        // window use the bag's place among the equipped bags (0, 1, 2...) so they work either way.
        public int PlaceOf(int bag) { int p = ActiveBags.IndexOf(bag); return p < 0 ? bag : p; }
        public bool IsVertical(int bag) { bag = PlaceOf(bag); return bag >= 0 && bag < 30 && ((cfg.VertMask >> bag) & 1) == 1; }
        public void SetVertical(int bag, bool v) { bag = PlaceOf(bag); if (bag < 0 || bag >= 30) return; if (v) cfg.VertMask |= (1 << bag); else cfg.VertMask &= ~(1 << bag); }
        private readonly List<BagRow> rows = new List<BagRow>();
        private string lastSig = "";
        private ActionConsumable<KeyCombination> origInv;
        private bool hooked, logged;

        public override bool ShouldLoad(EnumAppSide forSide) { return forSide == EnumAppSide.Client; }

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            try { BagsConfig c = api.LoadModConfig<BagsConfig>("betterbags.json"); if (c != null) cfg = c; } catch (Exception) { }
            craft = new CraftDialog(api, this);
            menu = new BagsMenu(api, this);
            api.Gui.RegisterDialog(menu);
            api.ChatCommands.Create("bagsmenu").WithDescription("Open the Better Bags settings")
                .HandleWith(delegate (TextCommandCallingArgs a) { menu.Toggle(); return TextCommandResult.Success("Better Bags settings"); });
            api.ChatCommands.Create("bags").WithDescription("Show or hide the bag rows")
                .HandleWith(delegate (TextCommandCallingArgs a)
                {
                    cfg.Hidden = !cfg.Hidden; Save(); lastSig = "";
                    return TextCommandResult.Success(cfg.Hidden ? "Bag rows hidden" : "Bag rows shown");
                });
            api.ChatCommands.Create("bagsmode").WithDescription("Switch between showing the bag rows only with the inventory open, or all the time")
                .HandleWith(delegate (TextCommandCallingArgs a)
                {
                    cfg.OnlyWithInventory = !cfg.OnlyWithInventory; Save(); Refresh();
                    return TextCommandResult.Success(cfg.OnlyWithInventory ? "Bag rows now show only while your inventory is open" : "Bag rows now show all the time");
                });
            api.ChatCommands.Create("bagsreset").WithDescription("Put the bag rows back in their default places")
                .HandleWith(delegate (TextCommandCallingArgs a)
                {
                    cfg.Pos = ""; Save(); lastSig = "";
                    return TextCommandResult.Success("Bag rows reset");
                });
            api.Event.LevelFinalize += HookTab;
            api.Event.RegisterGameTickListener(delegate (float dt)
            {
                try { Tick(); } catch (Exception e) { if (!logged) { logged = true; capi.Logger.Warning("[betterbags] " + e); } }
            }, 120);
        }

        // Tab (the inventory key) now opens only the crafting grid
        private void HookTab()
        {
            if (hooked) return;
            try
            {
                HotKey hk = capi.Input.GetHotKeyByCode("inventorydialog");
                if (hk != null)
                {
                    FieldInfo fh = hk.GetType().GetField("Handler", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (fh != null) origInv = fh.GetValue(hk) as ActionConsumable<KeyCombination>;
                }
                capi.Input.SetHotKeyHandler("inventorydialog", OnInventoryKey);
                hooked = true;
                capi.Logger.Notification("[betterbags] Tab now opens the crafting grid only");
            }
            catch (Exception e) { capi.Logger.Warning("[betterbags] could not hook the inventory key: " + e.Message); }
        }

        // With OnlyWithInventory, the rows show while the crafting grid (opened by the inventory key) or the settings window is open
        private bool RowsWanted()
        {
            return (craft != null && craft.IsOpened()) || (menu != null && menu.IsOpened());
        }

        private bool OnInventoryKey(KeyCombination kc)
        {
            try
            {
                IClientPlayer p = capi.World.Player;
                if (p != null && p.WorldData != null && p.WorldData.CurrentGameMode == EnumGameMode.Creative && origInv != null)
                    return origInv(kc);      // creative keeps the normal inventory screen
            }
            catch (Exception) { }
            craft.Toggle();
            Refresh();      // show or hide the bag rows straight away, not at the next tick
            return true;
        }

        // ---- positions ----
        public bool TryGetPos(int bag, out double x, out double y)
        {
            x = 0; y = 0;
            bag = PlaceOf(bag);
            if (string.IsNullOrEmpty(cfg.Pos)) return false;
            string[] parts = cfg.Pos.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                string[] f = parts[i].Split(':');
                if (f.Length != 3) continue;
                int b; double px, py;
                if (!int.TryParse(f[0], out b) || b != bag) continue;
                if (!double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out px)) continue;
                if (!double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out py)) continue;
                x = px; y = py;
                return OnScreen(x, y);   // a row saved off screen (e.g. a different window size) goes back to its default spot
            }
            return false;
        }

        public void SavePos(int bag, double x, double y)
        {
            bag = PlaceOf(bag);
            StringBuilder sb = new StringBuilder();
            string[] parts = string.IsNullOrEmpty(cfg.Pos) ? new string[0] : cfg.Pos.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                string[] f = parts[i].Split(':');
                int b; if (f.Length != 3 || !int.TryParse(f[0], out b) || b == bag) continue;
                sb.Append(parts[i]).Append(';');
            }
            sb.Append(bag.ToString()).Append(':').Append(x.ToString("0.#", CultureInfo.InvariantCulture)).Append(':').Append(y.ToString("0.#", CultureInfo.InvariantCulture));
            cfg.Pos = sb.ToString();
            Save();
        }

        public void DefaultPos(int bag, out double x, out double y)
        {
            bag = PlaceOf(bag);
            double w = capi.Render.FrameWidth / RuntimeEnv.GUIScale;
            double h = capi.Render.FrameHeight / RuntimeEnv.GUIScale;
            x = w / 2 - 210;
            y = h - 130 - (bag + 1) * 72;
            if (y < 10) y = 10;
            if (x < 0) x = 0;
        }

        private bool OnScreen(double x, double y)
        {
            double w = capi.Render.FrameWidth / RuntimeEnv.GUIScale;
            double h = capi.Render.FrameHeight / RuntimeEnv.GUIScale;
            return x >= -50 && y >= 0 && x < w - 40 && y < h - 20;
        }

        public void Save() { try { capi.StoreModConfig(cfg, "betterbags.json"); } catch (Exception) { } }

        // a row found that its slots are gone (the bag was taken out): rebuild at the next tick
        public void Invalidate() { lastSig = "#stale"; }

        // rebuild the rows right now (used by the settings window, because ticks stop while the game is paused)
        public void Refresh() { lastSig = "#stale"; try { Tick(); } catch (Exception e) { capi.Logger.Warning("[betterbags] " + e.Message); } }

        public void ResetPositions() { cfg.Pos = ""; cfg.CraftPos = ""; Save(); Refresh(); if (craft != null) craft.ResetPos(); }

        public static readonly string[] BorderNames = { "Off", "Thin", "Medium", "Thick", "Invisible" };
        public static readonly string[] BorderColorNames = { "Gold", "White", "Black", "Red", "Blue", "Green", "Brown", "Purple" };
        public static readonly double[][] BorderColors = {
            new double[] { 0.85, 0.70, 0.30 }, new double[] { 0.95, 0.95, 0.95 }, new double[] { 0.04, 0.04, 0.04 }, new double[] { 0.80, 0.20, 0.20 },
            new double[] { 0.30, 0.50, 0.90 }, new double[] { 0.30, 0.75, 0.35 }, new double[] { 0.50, 0.33, 0.18 }, new double[] { 0.60, 0.35, 0.80 } };
        public static readonly string[] BgColorNames = { "Dark", "Black", "Brown", "Blue", "Grey", "Green" };
        public static readonly double[][] BgColors = {
            new double[] { 0.12, 0.10, 0.08 }, new double[] { 0, 0, 0 }, new double[] { 0.30, 0.20, 0.12 },
            new double[] { 0.10, 0.15, 0.28 }, new double[] { 0.25, 0.25, 0.25 }, new double[] { 0.10, 0.22, 0.12 } };

        // ---- bag rows ----
        public static int BagIndexOf(ItemSlot s)
        {
            Type t = s.GetType();
            if (t.Name.IndexOf("BagContent", StringComparison.Ordinal) < 0) return -1;
            BindingFlags f = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            FieldInfo fi = t.GetField("BagIndex", f);
            if (fi != null) { try { return Convert.ToInt32(fi.GetValue(s)); } catch (Exception) { } }
            PropertyInfo pi = t.GetProperty("BagIndex", f);
            if (pi != null) { try { return Convert.ToInt32(pi.GetValue(s, null)); } catch (Exception) { } }
            return -1;
        }

        private void Tick()
        {
            if (cfg.Hidden || (cfg.OnlyWithInventory && !RowsWanted())) { if (rows.Count > 0) ClearRows(); lastSig = ""; return; }
            IClientPlayer player = capi.World.Player;
            if (player == null) return;
            IInventory inv = player.InventoryManager.GetOwnInventory(GlobalConstants.backpackInvClassName);
            if (inv == null) return;
            Dictionary<int, List<int>> bags = new Dictionary<int, List<int>>();
            for (int j = 0; j < inv.Count; j++)
            {
                ItemSlot s = inv[j];
                if (s == null) continue;
                int b = BagIndexOf(s);
                if (b < 0) continue;
                List<int> ids;
                if (!bags.TryGetValue(b, out ids)) { ids = new List<int>(); bags[b] = ids; }
                ids.Add(j);
            }
            List<int> keys = new List<int>(bags.Keys);
            keys.Sort();
            ActiveBags = new List<int>(keys);
            StringBuilder sig = new StringBuilder();
            for (int k = 0; k < keys.Count; k++) { List<int> v = bags[keys[k]]; sig.Append(keys[k]).Append('=').Append(v.Count).Append(',').Append(v[0]).Append(';'); }
            string ss = sig.ToString();
            if (ss == lastSig) return;
            lastSig = ss;
            ClearRows();
            for (int k = 0; k < keys.Count; k++)
            {
                List<int> v = bags[keys[k]];
                if (v.Count == 0) continue;
                BagRow row = new BagRow(capi, this, inv, keys[k], v.ToArray());
                rows.Add(row);
                row.TryOpen();
            }
            capi.Logger.Notification("[betterbags] bag rows: " + bags.Count + " (" + ss + ")");
        }

        private void ClearRows()
        {
            for (int i = 0; i < rows.Count; i++) { try { rows[i].TryClose(); rows[i].Dispose(); } catch (Exception) { } }
            rows.Clear();
            if (cfg.Hidden) lastSig = "";
        }
    }

    // One floating hotbar style row per bag. Drag the thin bar on top of it to move it.
    public class BagRow : GuiDialog
    {
        private BetterBagsSystem sys;
        private IInventory inv;
        private int bag;
        private int[] slotIds;
        private ElementBounds handle, gridB;
        private bool dragging;

        public override string ToggleKeyCombinationCode { get { return null; } }
        public override bool PrefersUngrabbedMouse { get { return false; } }
        public override EnumDialogType DialogType { get { return EnumDialogType.HUD; } }

        public BagRow(ICoreClientAPI capi, BetterBagsSystem sys, IInventory inv, int bag, int[] slotIds) : base(capi)
        {
            this.sys = sys; this.inv = inv; this.bag = bag; this.slotIds = slotIds;
        }

        private bool dead;
        private string contentSig = "";

        // what is inside this bag's slots right now; when it changes the row is drawn again so new items show at once
        private string ContentSig()
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < slotIds.Length; i++)
            {
                ItemSlot sl = inv[slotIds[i]];
                ItemStack st = sl == null ? null : sl.Itemstack;
                if (st == null || st.Collectible == null) sb.Append('-');
                else sb.Append(st.Collectible.Id).Append('x').Append(st.StackSize);
                sb.Append(',');
            }
            return sb.ToString();
        }

        private void RefreshIfChanged()
        {
            if (dragging || SingleComposer == null) return;
            try
            {
                string cs = ContentSig();
                if (cs != contentSig) { contentSig = cs; Compose(); }
            }
            catch (Exception) { }
        }

        // every slot this row shows must still exist and still belong to its bag, otherwise the game would draw a missing slot and crash
        private bool Valid()
        {
            if (dead) return false;
            try
            {
                for (int i = 0; i < slotIds.Length; i++)
                {
                    int id = slotIds[i];
                    if (id < 0 || id >= inv.Count) { dead = true; break; }
                    ItemSlot sl = inv[id];
                    if (sl == null || BetterBagsSystem.BagIndexOf(sl) != bag) { dead = true; break; }
                }
            }
            catch (Exception) { dead = true; }
            if (dead) sys.Invalidate();
            return !dead;
        }

        public override void OnRenderGUI(float deltaTime)
        {
            if (!Valid()) return;
            RefreshIfChanged();
            SlotScale.Push(capi, sys.cfg.SlotPct);
            try { base.OnRenderGUI(deltaTime); } finally { SlotScale.Pop(); }
        }
        public override void OnFinalizeFrame(float dt)
        {
            if (!Valid()) return;
            SlotScale.Push(capi, sys.cfg.SlotPct);
            try { base.OnFinalizeFrame(dt); } finally { SlotScale.Pop(); }
        }

        public override void OnGuiOpened() { base.OnGuiOpened(); Compose(); }

        private void Compose()
        {
            double x, y;
            if (SingleComposer != null) { x = SingleComposer.Bounds.fixedX; y = SingleComposer.Bounds.fixedY; }
            else if (!sys.TryGetPos(bag, out x, out y)) sys.DefaultPos(bag, out x, out y);
            try { contentSig = ContentSig(); } catch (Exception) { }
            int n = slotIds.Length;
            int cols, rowsN;
            if (sys.IsVertical(bag))
            {
                rowsN = Math.Max(1, Math.Min(Math.Min(8, n), sys.cfg.PerColumn));
                cols = (n + rowsN - 1) / rowsN;
                rowsN = (n + cols - 1) / cols;
            }
            else
            {
                cols = Math.Max(1, Math.Min(Math.Min(8, n), sys.cfg.PerRow));
                rowsN = (n + cols - 1) / cols;
            }
            SlotScale.Push(capi, sys.cfg.SlotPct);
            ElementBounds grid;
            try { grid = ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, 12, cols, rowsN); }
            finally { SlotScale.Pop(); }
            gridB = grid;
            handle = ElementBounds.Fixed(0, 0, grid.fixedWidth, 8);
            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.None);
            dialogBounds.fixedX = x; dialogBounds.fixedY = y;
            ElementBounds bg = ElementBounds.Fill.WithFixedPadding(6);
            bg.BothSizing = ElementSizing.FitToChildren;
            GuiComposer comp = capi.Gui.CreateCompo("betterbag" + bag, dialogBounds)
                .AddStaticCustomDraw(bg, DrawPanel)
                .BeginChildElements(bg);
            // Invisible style: no panel, no border and no visible bar, only the slots; the top strip still drags the row
            if (sys.cfg.BorderStyle == 4) comp.AddStaticCustomDraw(handle, delegate (Cairo.Context ctx, Cairo.ImageSurface surface, ElementBounds b2) { });
            else comp.AddInset(handle, 2);
            comp.AddItemSlotGrid(inv, SendInvPacket, cols, slotIds, grid, "bagslots" + bag)
                .EndChildElements();
            if (SingleComposer != null) SingleComposer.Dispose();
            SlotScale.Push(capi, sys.cfg.SlotPct);
            try { SingleComposer = comp.Compose(); }
            finally { SlotScale.Pop(); }
        }

        private void SendInvPacket(object packet) { capi.Network.SendPacketClient(packet); }

        // the panel behind the slots: colour, see through amount, border and corners from the settings
        private void DrawPanel(Cairo.Context ctx, Cairo.ImageSurface surface, ElementBounds b) { Paint(sys.cfg, ctx, b); }

        public static void Paint(BagsConfig c, Cairo.Context ctx, ElementBounds b)
        {
            double gs = Math.Max(0.5, RuntimeEnv.GUIScale);
            double w = b.OuterWidth, h = b.OuterHeight;
            double[] bgc = BetterBagsSystem.BgColors[Math.Max(0, Math.Min(BetterBagsSystem.BgColors.Length - 1, c.BgColor))];
            double[] bc = BetterBagsSystem.BorderColors[Math.Max(0, Math.Min(BetterBagsSystem.BorderColors.Length - 1, c.BorderColor))];
            double[] widths = { 0, 1.5, 3, 5, 0 };
            double bw = widths[Math.Max(0, Math.Min(4, c.BorderStyle))] * gs;
            double alpha = c.BorderStyle == 4 ? 0 : 1.0 - Math.Max(0, Math.Min(100, c.Transparency)) / 100.0;
            double r = c.Rounded ? 8 * gs : 0;
            double x0 = bw / 2, y0 = bw / 2, x1 = w - bw / 2, y1 = h - bw / 2;
            if (r > 0)
            {
                ctx.NewPath();
                ctx.Arc(x1 - r, y0 + r, r, -Math.PI / 2, 0);
                ctx.Arc(x1 - r, y1 - r, r, 0, Math.PI / 2);
                ctx.Arc(x0 + r, y1 - r, r, Math.PI / 2, Math.PI);
                ctx.Arc(x0 + r, y0 + r, r, Math.PI, 1.5 * Math.PI);
                ctx.ClosePath();
            }
            else
            {
                ctx.NewPath();
                ctx.Rectangle(x0, y0, x1 - x0, y1 - y0);
            }
            if (alpha > 0.001)
            {
                ctx.SetSourceRGBA(bgc[0], bgc[1], bgc[2], alpha);
                ctx.FillPreserve();
            }
            if (bw > 0)
            {
                ctx.SetSourceRGBA(bc[0], bc[1], bc[2], 1);
                ctx.LineWidth = bw;
                ctx.Stroke();
            }
            else ctx.NewPath();
        }

        // clicks that are not on the drag bar or a slot go through to whatever is underneath (for example the Better Bars bars)
        private bool Over(MouseEvent args)
        {
            try
            {
                if (handle != null && handle.PointInside(args.X, args.Y)) return true;
                if (gridB != null && gridB.PointInside(args.X, args.Y)) return true;
            }
            catch (Exception) { return true; }
            return false;
        }

        public override void OnMouseDown(MouseEvent args)
        {
            if (!Valid()) return;
            if (!dragging && !Over(args)) return;
            if (handle != null && handle.PointInside(args.X, args.Y)) { dragging = true; args.Handled = true; return; }
            SlotScale.Push(capi, sys.cfg.SlotPct);
            try { base.OnMouseDown(args); } finally { SlotScale.Pop(); }
        }

        public override void OnMouseMove(MouseEvent args)
        {
            if (dead) return;
            if (!dragging && !Over(args)) return;
            if (dragging && SingleComposer != null)
            {
                SingleComposer.Bounds.fixedX += args.DeltaX / RuntimeEnv.GUIScale;
                SingleComposer.Bounds.fixedY += args.DeltaY / RuntimeEnv.GUIScale;
                SingleComposer.Bounds.CalcWorldBounds();
                args.Handled = true;
                return;
            }
            SlotScale.Push(capi, sys.cfg.SlotPct);
            try { base.OnMouseMove(args); } finally { SlotScale.Pop(); }
        }

        public override void OnMouseUp(MouseEvent args)
        {
            if (dead) return;
            if (!dragging && !Over(args)) return;
            if (dragging)
            {
                dragging = false;
                if (SingleComposer != null) sys.SavePos(bag, SingleComposer.Bounds.fixedX, SingleComposer.Bounds.fixedY);
                args.Handled = true;
                return;
            }
            SlotScale.Push(capi, sys.cfg.SlotPct);
            try { base.OnMouseUp(args); } finally { SlotScale.Pop(); }
        }
    }

    // the settings window (type .bagsmenu, or pick Better Bags in the yayos1 drop down)
    public class BagsMenu : GuiDialog
    {
        private BetterBagsSystem sys;
        private bool loading;
        public override string ToggleKeyCombinationCode { get { return null; } }
        public override bool PrefersUngrabbedMouse { get { return true; } }
        public BagsMenu(ICoreClientAPI capi, BetterBagsSystem sys) : base(capi) { this.sys = sys; }
        public void Toggle() { if (IsOpened()) TryClose(); else TryOpen(); }
        public override void OnGuiOpened() { base.OnGuiOpened(); Compose(); }

        private void Txt(string key, string text) { try { SingleComposer.GetDynamicText(key).SetNewText(text); } catch (Exception) { } }
        private bool Changed() { sys.Save(); sys.Refresh(); return true; }
        private bool Again() { sys.Save(); sys.Refresh(); Compose(); return true; }

        private void Compose()
        {
            CairoFont f = CairoFont.WhiteSmallText();
            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;
            double y = GuiStyle.TitleBarHeight;
            BagsConfig c = sys.cfg;
            GuiComposer comp = capi.Gui.CreateCompo("betterbagsmenu", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar("Better Bags", delegate () { TryClose(); })
                .BeginChildElements(bgBounds);
            comp.AddStaticText("Width", f, ElementBounds.Fixed(0, y, 90, 24));
            comp.AddSlider(delegate (int v) { if (loading) return true; c.PerRow = v; Txt("vw", v + " slots"); return Changed(); }, ElementBounds.Fixed(95, y + 2, 220, 20), "w");
            comp.AddDynamicText("", f, ElementBounds.Fixed(325, y, 70, 24), "vw");
            y += 30;
            comp.AddStaticText("Height", f, ElementBounds.Fixed(0, y, 90, 24));
            comp.AddSlider(delegate (int v) { if (loading) return true; c.PerColumn = v; Txt("vh", v + " slots"); return Changed(); }, ElementBounds.Fixed(95, y + 2, 220, 20), "h");
            comp.AddDynamicText("", f, ElementBounds.Fixed(325, y, 70, 24), "vh");
            y += 30;
            comp.AddStaticText("Slot size", f, ElementBounds.Fixed(0, y, 90, 24));
            comp.AddSlider(delegate (int v) { if (loading) return true; c.SlotPct = v; Txt("vsize", v + "%"); return Changed(); }, ElementBounds.Fixed(95, y + 2, 220, 20), "size");
            comp.AddDynamicText("", f, ElementBounds.Fixed(325, y, 70, 24), "vsize");
            y += 30;
            comp.AddStaticText("Transparent", f, ElementBounds.Fixed(0, y, 90, 24));
            comp.AddSlider(delegate (int v) { if (loading) return true; c.Transparency = v; Txt("vtr", v + "%"); return Changed(); }, ElementBounds.Fixed(95, y + 2, 220, 20), "tr");
            comp.AddDynamicText("", f, ElementBounds.Fixed(325, y, 70, 24), "vtr");
            y += 36;
            List<int> bl = sys.ActiveBags;
            if (bl.Count == 0) { comp.AddStaticText("No bag equipped (equip a backpack to set its layout)", CairoFont.WhiteDetailText(), ElementBounds.Fixed(0, y + 2, 400, 20)); y += 30; }
            for (int k = 0; k < bl.Count; k++)
            {
                int bagIdx = bl[k];
                comp.AddSmallButton("Bag " + (k + 1) + ": " + (sys.IsVertical(bagIdx) ? "Vertical" : "Horizontal"), delegate () { sys.SetVertical(bagIdx, !sys.IsVertical(bagIdx)); return Again(); }, ElementBounds.Fixed((k % 2) * 200, y + (k / 2) * 32, 195, 26), EnumButtonStyle.Normal, "blay" + k);
            }
            if (bl.Count > 0) y += ((bl.Count + 1) / 2) * 32;
            comp.AddSmallButton("Corners: " + (c.Rounded ? "Round" : "Square"), delegate () { c.Rounded = !c.Rounded; return Again(); }, ElementBounds.Fixed(0, y, 195, 26), EnumButtonStyle.Normal, "bcor");
            comp.AddSmallButton("Panel colour: " + BetterBagsSystem.BgColorNames[Math.Max(0, Math.Min(BetterBagsSystem.BgColorNames.Length - 1, c.BgColor))], delegate () { c.BgColor = (c.BgColor + 1) % BetterBagsSystem.BgColorNames.Length; return Again(); }, ElementBounds.Fixed(200, y, 195, 26), EnumButtonStyle.Normal, "bbg");
            y += 32;
            comp.AddSmallButton("Border: " + BetterBagsSystem.BorderNames[Math.Max(0, Math.Min(4, c.BorderStyle))], delegate () { c.BorderStyle = (c.BorderStyle + 1) % BetterBagsSystem.BorderNames.Length; return Again(); }, ElementBounds.Fixed(0, y, 195, 26), EnumButtonStyle.Normal, "bbor");
            comp.AddSmallButton("Border colour: " + BetterBagsSystem.BorderColorNames[Math.Max(0, Math.Min(BetterBagsSystem.BorderColorNames.Length - 1, c.BorderColor))], delegate () { c.BorderColor = (c.BorderColor + 1) % BetterBagsSystem.BorderColorNames.Length; return Again(); }, ElementBounds.Fixed(200, y, 195, 26), EnumButtonStyle.Normal, "bbc");
            y += 32;
            comp.AddSmallButton("Bag rows: " + (c.Hidden ? "hidden" : "shown"), delegate () { c.Hidden = !c.Hidden; return Again(); }, ElementBounds.Fixed(0, y, 195, 26), EnumButtonStyle.Normal, "bhid");
            comp.AddSmallButton("Reset positions", delegate () { sys.ResetPositions(); return true; }, ElementBounds.Fixed(200, y, 195, 26), EnumButtonStyle.Normal, "brst");
            y += 32;
            comp.AddSmallButton("Reset everything", delegate () { ResetAll(); return true; }, ElementBounds.Fixed(0, y, 195, 26), EnumButtonStyle.Normal, "brall");
            y += 34;
            comp.AddStaticText("Width = slots per row (Horizontal bags). Height = slots per column (Vertical bags).", CairoFont.WhiteDetailText(), ElementBounds.Fixed(0, y, 400, 20));
            y += 20;
            comp.AddStaticText("Drag the thin bar on top of a bag row to move it.", CairoFont.WhiteDetailText(), ElementBounds.Fixed(0, y, 400, 20));
            comp.EndChildElements();
            if (SingleComposer != null) SingleComposer.Dispose();
            SingleComposer = comp.Compose();
            loading = true;
            try
            {
                SingleComposer.GetSlider("w").SetValues(c.PerRow, 1, 8, 1, "");
                SingleComposer.GetDynamicText("vw").SetNewText(c.PerRow + " slots");
                SingleComposer.GetSlider("h").SetValues(c.PerColumn, 1, 8, 1, "");
                SingleComposer.GetDynamicText("vh").SetNewText(c.PerColumn + " slots");
                SingleComposer.GetSlider("size").SetValues(c.SlotPct, 50, 150, 5, "");
                SingleComposer.GetDynamicText("vsize").SetNewText(c.SlotPct + "%");
                SingleComposer.GetSlider("tr").SetValues(c.Transparency, 0, 100, 5, "");
                SingleComposer.GetDynamicText("vtr").SetNewText(c.Transparency + "%");
            }
            catch (Exception) { }
            loading = false;
        }

        private void ResetAll()
        {
            BagsConfig d = new BagsConfig();
            d.Pos = "";
            sys.cfg.Hidden = d.Hidden; sys.cfg.VertMask = 0; sys.cfg.PerRow = d.PerRow; sys.cfg.PerColumn = d.PerColumn;
            sys.cfg.SlotPct = d.SlotPct; sys.cfg.Transparency = d.Transparency; sys.cfg.BorderStyle = d.BorderStyle;
            sys.cfg.BorderColor = d.BorderColor; sys.cfg.BgColor = d.BgColor; sys.cfg.Rounded = d.Rounded; sys.cfg.Pos = ""; sys.cfg.CraftPos = "";
            sys.Save(); sys.Refresh(); Compose();
        }
    }

    // Tab opens only this: the 3x3 crafting grid and its output slot, in the same style as the bag rows
    public class CraftDialog : GuiDialog
    {
        private IInventory inv;
        private BetterBagsSystem sys;
        private string styleSig = "";
        private ElementBounds handle;
        private bool dragging;
        private bool forceDefault;
        public void ResetPos() { forceDefault = true; if (IsOpened()) Compose(); }
        public override string ToggleKeyCombinationCode { get { return null; } }
        public override bool PrefersUngrabbedMouse { get { return true; } }

        public CraftDialog(ICoreClientAPI capi, BetterBagsSystem sys) : base(capi) { this.sys = sys; }

        public void Toggle() { if (IsOpened()) TryClose(); else TryOpen(); }

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            inv = capi.World.Player.InventoryManager.GetOwnInventory(GlobalConstants.craftingInvClassName);
            Compose();
            if (inv != null) capi.World.Player.InventoryManager.OpenInventory(inv);
        }

        public override void OnGuiClosed()
        {
            if (inv != null) capi.World.Player.InventoryManager.CloseInventory(inv);
            base.OnGuiClosed();
            sys.Refresh();
        }

        private string Sig()
        {
            BagsConfig c = sys.cfg;
            return c.SlotPct + "/" + c.Transparency + "/" + c.BorderStyle + "/" + c.BorderColor + "/" + c.BgColor + "/" + c.Rounded;
        }

        public override void OnRenderGUI(float deltaTime)
        {
            if (Sig() != styleSig) Compose();
            SlotScale.Push(capi, sys.cfg.SlotPct);
            try { base.OnRenderGUI(deltaTime); } finally { SlotScale.Pop(); }
        }

        public override void OnFinalizeFrame(float dt)
        {
            SlotScale.Push(capi, sys.cfg.SlotPct);
            try { base.OnFinalizeFrame(dt); } finally { SlotScale.Pop(); }
        }

        public override void OnMouseDown(MouseEvent args)
        {
            if (handle != null && handle.PointInside(args.X, args.Y)) { dragging = true; args.Handled = true; return; }
            SlotScale.Push(capi, sys.cfg.SlotPct); try { base.OnMouseDown(args); } finally { SlotScale.Pop(); }
        }

        public override void OnMouseMove(MouseEvent args)
        {
            if (dragging && SingleComposer != null)
            {
                SingleComposer.Bounds.fixedX += args.DeltaX / RuntimeEnv.GUIScale;
                SingleComposer.Bounds.fixedY += args.DeltaY / RuntimeEnv.GUIScale;
                SingleComposer.Bounds.CalcWorldBounds();
                args.Handled = true;
                return;
            }
            SlotScale.Push(capi, sys.cfg.SlotPct); try { base.OnMouseMove(args); } finally { SlotScale.Pop(); }
        }

        public override void OnMouseUp(MouseEvent args)
        {
            if (dragging)
            {
                dragging = false;
                if (SingleComposer != null)
                {
                    sys.cfg.CraftPos = SingleComposer.Bounds.fixedX.ToString("0.#", CultureInfo.InvariantCulture) + ":" + SingleComposer.Bounds.fixedY.ToString("0.#", CultureInfo.InvariantCulture);
                    sys.Save();
                }
                args.Handled = true;
                return;
            }
            SlotScale.Push(capi, sys.cfg.SlotPct); try { base.OnMouseUp(args); } finally { SlotScale.Pop(); }
        }

        private void Compose()
        {
            styleSig = Sig();
            double p = 51.0 * sys.cfg.SlotPct / 100.0;
            double gs0 = Math.Max(0.5, RuntimeEnv.GUIScale);
            double totW = 4 * p + 44 + 12, totH = 3 * p + 12 + 14;
            double px, py;
            if (SingleComposer != null && !forceDefault) { px = SingleComposer.Bounds.fixedX; py = SingleComposer.Bounds.fixedY; }
            else
            {
                px = capi.Render.FrameWidth / gs0 / 2 - totW / 2; py = capi.Render.FrameHeight / gs0 / 2 - totH / 2;
                string[] cp = (sys.cfg.CraftPos ?? "").Split(':'); double sx, sy;
                if (!forceDefault && cp.Length == 2 && double.TryParse(cp[0], NumberStyles.Float, CultureInfo.InvariantCulture, out sx) && double.TryParse(cp[1], NumberStyles.Float, CultureInfo.InvariantCulture, out sy)) { px = sx; py = sy; }
            }
            forceDefault = false;
            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.None);
            dialogBounds.fixedX = px; dialogBounds.fixedY = py;
            ElementBounds bg = ElementBounds.Fill.WithFixedPadding(6);
            bg.BothSizing = ElementSizing.FitToChildren;
            ElementBounds grid, outB;
            SlotScale.Push(capi, sys.cfg.SlotPct);
            try
            {
                grid = ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, 12, 3, 3);
                outB = ElementStdBounds.SlotGrid(EnumDialogArea.None, 3 * p + 44, 12 + p, 1, 1);
            }
            finally { SlotScale.Pop(); }
            ElementBounds arrow = ElementBounds.Fixed(3 * p + 8, 12 + p + (p - 34) / 2, 34, 34);
            handle = ElementBounds.Fixed(0, 0, 3 * p + 44 + p - 3, 8);
            GuiComposer comp = capi.Gui.CreateCompo("betterbagscraft", dialogBounds)
                .AddStaticCustomDraw(bg, delegate (Cairo.Context ctx, Cairo.ImageSurface surface, ElementBounds b) { BagRow.Paint(sys.cfg, ctx, b); })
                .BeginChildElements(bg);
            if (sys.cfg.BorderStyle == 4) comp.AddStaticCustomDraw(handle, delegate (Cairo.Context ctx, Cairo.ImageSurface surface, ElementBounds b2) { });
            else comp.AddInset(handle, 2);
            if (inv != null)
            {
                comp.AddItemSlotGrid(inv, SendInvPacket, 3, new int[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 }, grid, "craftgrid");
                comp.AddItemSlotGrid(inv, SendInvPacket, 1, new int[] { 9 }, outB, "craftout");
            }
            comp.AddStaticText("→", CairoFont.WhiteMediumText(), arrow);
            comp.EndChildElements();
            if (SingleComposer != null) SingleComposer.Dispose();
            SlotScale.Push(capi, sys.cfg.SlotPct);
            try { SingleComposer = comp.Compose(); }
            finally { SlotScale.Pop(); }
        }

        private void SendInvPacket(object packet) { capi.Network.SendPacketClient(packet); }
    }
}
