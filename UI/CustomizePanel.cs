using System.Collections.Generic;
using HarmonyLib;
using malafein.Valheim.Shared;
using malafein.Valheim.SharedUI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace malafein.Valheim.ShipwrightsTouch
{
    // The ship customization panel: name, owner, sail texture and sail color for one ship.
    // Choices preview live on the real ship (ColoringPatches asks TryGetPreview) and are only
    // written to the ship's ZDO on Apply; Cancel or Esc drops them.
    public static class CustomizePanel
    {
        private static CustomizePanelController s_controller;

        // Set on the frame the panel closes, so it still counts as open for the rest of that
        // frame (see ModalPanels: stops Esc from also opening the game menu).
        private static int s_closedFrame = -1;

        public static bool IsOpen => (s_controller != null && s_controller.IsOpen) || Time.frameCount == s_closedFrame;

        public static void Open(Ship ship)
        {
            if (s_controller == null)
            {
                Log.Warn("Customization panel isn't built yet (Hud not awake).");
                return;
            }
            s_controller.Open(ship);
        }

        private static bool IsOpenNow() => IsOpen;

        internal static void MarkClosed() => s_closedFrame = Time.frameCount;

        internal static bool TryGetPreview(Ship ship, out Color color, out Texture2D texture)
        {
            if (s_controller != null && s_controller.IsOpen && s_controller.Ship == ship)
            {
                color = s_controller.PreviewColor;
                texture = s_controller.PreviewTexture;
                return true;
            }
            color = Color.white;
            texture = null;
            return false;
        }

        [HarmonyPatch]
        private static class Patches
        {
            [HarmonyPatch(typeof(Hud), "Awake")]
            [HarmonyPostfix]
            private static void Postfix_HudAwake(Hud __instance)
            {
                if (__instance.m_rootObject == null) return;
                s_controller = CustomizePanelController.Build(__instance.m_rootObject.transform);
                // Hud wakes on every world load; a method group compares equal, so this registers once.
                ModalPanels.Register(IsOpenNow);
            }
        }
    }

    public class CustomizePanelController : MonoBehaviour
    {
        private const float PanelWidth = 480f;
        private const float PanelHeight = 680f;
        private const float Margin = 24f;
        private const float LabelWidth = 80f;
        private const float RowHeight = 52f;
        private const float SwatchSize = 36f;
        private const float RecentSwatchSize = 28f;
        private const float SwatchGap = 8f;
        private const int NameCharLimit = 20;

        // The panel closes if the player walks this far from the ship.
        private const float MaxDistance = 20f;

        private static readonly Color RowColor = new Color(0.3f, 0.3f, 0.3f, 0.35f);
        private static readonly Color RowSelectedColor = new Color(1f, 0.718f, 0.36f, 0.45f);
        private static readonly Color HintColor = new Color(0.75f, 0.75f, 0.75f, 1f);

        private class TextureRow
        {
            public string Hash;
            public Image Background;
            public RawImage Thumbnail;
        }

        private class Swatch
        {
            public Color Color;
            public Outline Outline;
        }

        public bool IsOpen => gameObject.activeSelf;
        public Ship Ship { get; private set; }
        public Color PreviewColor => _color;
        public Texture2D PreviewTexture => SailNetwork.Policy.AllowCustomTextures ? SailTextures.Get(_textureHash) : null;

        private TMP_InputField _nameField;
        private TMP_InputField _hexField;
        private TextMeshProUGUI _ownerText;
        private TextMeshProUGUI _ownerButtonLabel;
        private TextMeshProUGUI _textureHint;
        private RectTransform _textureList;
        private ScrollRect _textureScroll;
        private RectTransform _recentRow;
        private Button _moderateButton;

        private readonly List<TextureRow> _textureRows = new List<TextureRow>();
        private readonly List<Swatch> _presetSwatches = new List<Swatch>();
        private readonly List<Swatch> _recentSwatches = new List<Swatch>();
        private string _recentKey;
        private string _catalogKey;

        // Unsaved choices, written to the ZDO on Apply.
        private string _textureHash = "";
        private Color _color = Color.white;
        private long _ownerId;
        private string _ownerName = "";

        // ── Build ────────────────────────────────────────────────────────

        public static CustomizePanelController Build(Transform parent)
        {
            var go = new GameObject("ShipwrightsTouch_CustomizePanel", typeof(RectTransform), typeof(Image), typeof(CustomizePanelController));
            go.transform.SetParent(parent, false);

            // Docked left of centre, so the ship stays in view for the live preview.
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0f, 0.5f);
            rt.anchorMax = new Vector2(0f, 0.5f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.sizeDelta = new Vector2(PanelWidth, PanelHeight);
            rt.anchoredPosition = new Vector2(60f, 0f);

            var background = go.GetComponent<Image>();
            if (!VanillaUI.ApplyPanelBackground(background))
                background.color = new Color(0.05f, 0.05f, 0.05f, 0.95f);

            // Its own sorting layer, so HUD elements later in the HUD's draw order (food, health,
            // weight) can't draw over it. A nested canvas needs its own raycaster for clicks.
            go.AddComponent<Canvas>();
            go.AddComponent<GraphicRaycaster>();

            var controller = go.GetComponent<CustomizePanelController>();
            controller.BuildLayout();
            SailDownloads.Changed += controller.DownloadsChanged;
            SailUploads.Changed += controller.UploadsChanged;
            go.SetActive(false);
            return controller;
        }

        private void BuildLayout()
        {
            TMP_FontAsset font = VanillaUI.BodyFont;
            float contentWidth = PanelWidth - 2 * Margin;

            var title = UIBuilder.AddText(Place("Title", 14f, 44f), "Customize Ship", font, 30f, TextAlignmentOptions.Center);
            VanillaUI.ApplyTitleStyle(title);

            // Name
            AddLabel("NameLabel", "Name", 70f, 36f);
            _nameField = VanillaUI.CloneInputField((RectTransform)transform);
            if (_nameField != null)
            {
                PlaceBox((RectTransform)_nameField.transform, Margin + LabelWidth, 70f, contentWidth - LabelWidth, 36f);
                _nameField.characterLimit = NameCharLimit;
                if (_nameField.placeholder is TMP_Text placeholder) placeholder.text = "Unnamed ship";
            }

            // Owner
            AddLabel("OwnerLabel", "Owner", 114f, 36f);
            const float ownerButtonWidth = 130f;
            _ownerText = UIBuilder.AddText(PlaceBoxRect("OwnerText", Margin + LabelWidth, 114f, contentWidth - LabelWidth - ownerButtonWidth - 12f, 36f), "", font, 18f, TextAlignmentOptions.MidlineLeft);
            // Player names can be long; shrink rather than run under the button.
            _ownerText.enableAutoSizing = true;
            _ownerText.fontSizeMin = 12f;
            _ownerText.fontSizeMax = 18f;
            _ownerText.textWrappingMode = TextWrappingModes.NoWrap;
            Button ownerButton = UIBuilder.AddButton(transform, "OwnerButton", "", ToggleOwner, 16f);
            PlaceBox((RectTransform)ownerButton.transform, PanelWidth - Margin - ownerButtonWidth, 114f, ownerButtonWidth, 36f);
            _ownerButtonLabel = ownerButton.GetComponentInChildren<TextMeshProUGUI>();

            // Sail texture
            AddHeader("TextureHeader", "Sail texture", 166f);
            _moderateButton = UIBuilder.AddButton(transform, "ModerateButton", "Moderate", OpenModeration, 14f);
            PlaceBox((RectTransform)_moderateButton.transform, PanelWidth - Margin - 110f, 164f, 110f, 28f);
            RectTransform listBox = Place("TextureListBox", 196f, 250f);
            var listBackground = listBox.gameObject.AddComponent<Image>();
            listBackground.color = new Color(0f, 0f, 0f, 0.3f);
            listBackground.raycastTarget = false;
            _textureList = UIBuilder.BuildScrollableList(listBox, "TextureList", out GameObject listRoot);
            _textureScroll = listRoot.GetComponent<ScrollRect>();

            _textureHint = UIBuilder.AddText(Place("TextureHint", 450f, 22f), "", font, 14f, TextAlignmentOptions.MidlineLeft);
            _textureHint.color = HintColor;
            _textureHint.enableAutoSizing = true;
            _textureHint.fontSizeMin = 11f;
            _textureHint.fontSizeMax = 14f;

            // Sail color
            AddHeader("ColorHeader", "Sail color", 480f);
            for (int i = 0; i < SailStyle.Presets.Length; i++)
            {
                RectTransform swatchRt = PlaceBoxRect($"Preset_{SailStyle.PresetNames[i]}", Margin + i * (SwatchSize + SwatchGap), 512f, SwatchSize, SwatchSize);
                _presetSwatches.Add(AddSwatch(swatchRt, SailStyle.Presets[i]));
            }

            _hexField = VanillaUI.CloneInputField((RectTransform)transform);
            if (_hexField != null)
            {
                PlaceBox((RectTransform)_hexField.transform, PanelWidth - Margin - 130f, 512f, 130f, SwatchSize);
                _hexField.characterLimit = 7;
                _hexField.onValueChanged.AddListener(HexChanged);
                _hexField.onEndEdit.AddListener(_ => _hexField.SetTextWithoutNotify(SailStyle.ToHex(_color)));
            }

            AddLabel("RecentLabel", "Recent", 558f, RecentSwatchSize, 16f);
            _recentRow = PlaceBoxRect("RecentRow", Margin + LabelWidth, 558f, contentWidth - LabelWidth, RecentSwatchSize);

            // Footer
            Button cancel = UIBuilder.AddButton(transform, "Cancel", "Cancel", Close);
            PlaceFooterButton((RectTransform)cancel.transform, left: true);
            Button apply = UIBuilder.AddButton(transform, "Apply", "Apply", Apply);
            PlaceFooterButton((RectTransform)apply.transform, left: false);
        }

        // ── Open / Close ─────────────────────────────────────────────────

        public void Open(Ship ship)
        {
            ZNetView nview = ship != null ? ship.GetComponent<ZNetView>() : null;
            if (nview == null || !nview.IsValid()) return;

            ZDO zdo = nview.GetZDO();
            Ship = ship;
            _textureHash = zdo.GetString(Plugin.ZdoTextureKey);
            SailStyle.TryGetColor(zdo, out _color);
            _ownerId = zdo.GetLong(Plugin.ZdoOwnerIdKey, 0L);
            _ownerName = Plugin.PlainText(zdo.GetString(Plugin.ZdoOwnerNameKey));

            if (_nameField != null) _nameField.SetTextWithoutNotify(zdo.GetString(Plugin.ZdoNameKey));

            // Config changes apply on the next open.
            _textureScroll.scrollSensitivity = Plugin.ScrollSensitivity.Value;

            SailTextures.Refresh();
            SailNetwork.RequestRefresh();
            RebuildTextureRows();
            _recentKey = null;
            RefreshOwner();
            _moderateButton.gameObject.SetActive(SailModeration.CanModerate);
            RefreshColor();

            gameObject.SetActive(true);
            DrawAboveHud();
            ColoringPatches.UpdateSailAppearance(ship);
        }

        // Set after activation: Unity can drop overrideSorting set on an inactive canvas.
        private void DrawAboveHud()
        {
            var canvas = GetComponent<Canvas>();
            Canvas parentCanvas = transform.parent != null ? transform.parent.GetComponentInParent<Canvas>() : null;
            canvas.overrideSorting = true;
            canvas.sortingOrder = (parentCanvas != null ? parentCanvas.sortingOrder : 0) + 10;
        }

        public void Close()
        {
            Ship ship = Ship;
            Ship = null;
            gameObject.SetActive(false);
            CustomizePanel.MarkClosed();

            // Back to what's stored, now that the preview is gone.
            if (ship != null) ColoringPatches.UpdateSailAppearance(ship);
        }

        private void Apply()
        {
            Ship ship = Ship;
            ZNetView nview = ship != null ? ship.GetComponent<ZNetView>() : null;
            if (nview == null || !nview.IsValid() || !Plugin.CanModifyShip(ship, out _))
            {
                Close();
                return;
            }

            ZDO zdo = nview.GetZDO();
            if (_nameField != null) zdo.Set(Plugin.ZdoNameKey, _nameField.text.Trim());
            zdo.Set(Plugin.ZdoTextureKey, _textureHash);
            SailStyle.SetColor(zdo, _color);
            SailStyle.AddRecent(_color);
            zdo.Set(Plugin.ZdoOwnerIdKey, _ownerId);
            zdo.Set(Plugin.ZdoOwnerNameKey, _ownerName);

            // Submit on first use: a texture from the player's folder that the server lacks.
            SailUploads.Submit(_textureHash);

            Close();
        }

        private void Update()
        {
            if (ZInput.GetKeyDown(KeyCode.Escape, false))
            {
                Close();
                return;
            }

            Player player = Player.m_localPlayer;
            if (Ship == null || player == null || Vector3.Distance(player.transform.position, Ship.transform.position) > MaxDistance)
            {
                Close();
            }
        }

        // Moderators only: drops the unsaved choices and opens the moderation panel on the
        // selected texture.
        private void OpenModeration()
        {
            string hash = _textureHash;
            Close();
            ModerationPanel.Open(hash == "" ? null : hash);
        }

        // ── Owner ────────────────────────────────────────────────────────

        // Opening the panel already required the ship to be public or this player's, so the
        // button either claims a public ship or makes this player's ship public.
        private void ToggleOwner()
        {
            Player player = Player.m_localPlayer;
            if (player == null) return;

            if (_ownerId == 0L)
            {
                _ownerId = player.GetPlayerID();
                _ownerName = player.GetPlayerName();
            }
            else
            {
                _ownerId = 0L;
                _ownerName = "";
            }
            RefreshOwner();
        }

        private void RefreshOwner()
        {
            bool isPublic = _ownerId == 0L;
            _ownerText.text = isPublic ? "Public" : $"{_ownerName} (you)";
            if (_ownerButtonLabel != null) _ownerButtonLabel.text = isPublic ? "Claim" : "Make public";
        }

        // ── Textures ─────────────────────────────────────────────────────

        private void RebuildTextureRows()
        {
            foreach (Transform child in _textureList) Destroy(child.gameObject);
            _textureRows.Clear();

            AddTextureRow("", "Vanilla sail", SailAppearance.VanillaTextureOf(Ship));
            if (SailNetwork.Policy.AllowCustomTextures)
            {
                foreach (SailTextures.Entry entry in SailTextures.Entries)
                {
                    // A file the game can't use is listed (so the player knows why) but can't be picked.
                    Texture2D texture = SailTextures.Get(entry.Hash);
                    string problem = SailTextures.ProblemOf(entry.Hash);
                    if (problem != null)
                        AddTextureRow(entry.Hash, entry.Name + Tag(problem, "#E06A5A"), null, usable: false);
                    else
                        AddTextureRow(entry.Hash, entry.Name + StatusTag(entry.Hash), texture);
                }

                // The server's textures and other players', after the player's own; one row per
                // texture.
                foreach (CatalogEntry entry in SortedCatalog())
                {
                    if (SailTextures.HasLocalCopy(entry.Hash) || !SailDownloads.MayShow(entry.Hash)) continue;
                    AddTextureRow(entry.Hash, entry.Name + StatusTag(entry.Hash), SailDownloads.Thumbnail(entry.Hash));
                }
                SailDownloads.RequestThumbnails();
            }

            _catalogKey = CatalogKey();
            RefreshTextureSelection();
        }

        // Small grey text after a texture's name: where it comes from, or how sharing it went.
        private static string StatusTag(string hash)
        {
            if (SailTextures.IsBundled(hash)) return Tag("included", "#B0B0B0");
            CatalogEntry entry = SailDownloads.Find(hash);
            if (entry == null)
            {
                if (SailUploads.IsUploading(hash)) return Tag("sharing", "#B0B0B0");
                return SailUploads.RefusalOf(hash) != null ? Tag("not shared", "#B0B0B0") : "";
            }
            if (entry.Source == TextureSource.Server) return Tag("server", "#B0B0B0");

            string uploader = entry.Mine ? "" : Tag($"by {entry.UploaderName}", "#B0B0B0");
            switch (entry.Status)
            {
                case TextureStatus.Pending: return uploader + Tag("pending", "#E8C547");
                case TextureStatus.Denied: return uploader + Tag(entry.AutoDenied ? "auto-denied" : "denied", "#E06A5A");
                default: return entry.Mine ? Tag("shared", "#8FC97A") : uploader;
            }
        }

        private static string Tag(string text, string color) => $" <size=75%><color={color}>{text}</color></size>";

        private static List<CatalogEntry> SortedCatalog()
        {
            var entries = new List<CatalogEntry>(SailDownloads.Catalog);
            entries.Sort((a, b) => string.Compare(a.Name, b.Name, System.StringComparison.OrdinalIgnoreCase));
            return entries;
        }

        private static string CatalogKey()
        {
            var hashes = new List<string>();
            // Status included: an approval or denial changes the row's tag. Aliases too: a new one
            // can make one of the player's own textures show as shared.
            foreach (CatalogEntry entry in SailDownloads.Catalog) hashes.Add(entry.Hash + entry.Status + entry.Aliases.Count);
            hashes.Sort(System.StringComparer.Ordinal);
            return SailNetwork.Policy.AllowCustomTextures + ":" + Plugin.ShowOtherPlayersTextures.Value + ":" + string.Join(",", hashes);
        }

        // The panel is rebuilt with the Hud on every world load; drop the old one's listener.
        private void OnDestroy()
        {
            SailDownloads.Changed -= DownloadsChanged;
            SailUploads.Changed -= UploadsChanged;
        }

        // An upload started, finished or was refused: its tag and the hint change.
        private void UploadsChanged()
        {
            if (IsOpen) RebuildTextureRows();
        }

        // A new catalog rebuilds the list; a thumbnail arriving only fills in its row, so the
        // list doesn't jump while the player scrolls.
        private void DownloadsChanged()
        {
            if (!IsOpen) return;
            if (CatalogKey() != _catalogKey)
            {
                RebuildTextureRows();
                return;
            }
            foreach (TextureRow row in _textureRows)
            {
                if (row.Thumbnail.texture != null || row.Hash == "") continue;
                Texture2D thumbnail = SailDownloads.Thumbnail(row.Hash);
                if (thumbnail == null) continue;
                row.Thumbnail.texture = thumbnail;
                row.Thumbnail.enabled = true;
            }
        }

        private string TextureHint()
        {
            if (!SailNetwork.Policy.AllowCustomTextures) return "This server doesn't allow custom sail textures.";
            if (SailNetwork.ProtocolMismatch) return SailNetwork.MismatchText;
            if (SailNetwork.Mode == ServerMode.Vanilla) return "This server doesn't have Shipwright's Touch: custom sail textures show only for you.";
            CatalogEntry entry = SailDownloads.Find(_textureHash);
            bool local = SailTextures.IsLocal(_textureHash);

            // Every player with the mod has these, so a modded server lists them as its own.
            if (SailTextures.IsBundled(_textureHash) && (entry == null || entry.Source == TextureSource.Server))
                return SailNetwork.Mode == ServerMode.Modded
                    ? "Included with Shipwright's Touch: everyone sees it."
                    : "Included with Shipwright's Touch.";
            if (SailNetwork.Mode == ServerMode.Modded && (local || (entry != null && entry.Mine))) return SharingHint(_textureHash);
            if (entry != null && !local) return CatalogHint(entry);
            return SailTextures.Entries.Count == 0
                ? "Add PNG or JPG images to BepInEx/config/ShipwrightsTouch/sails"
                : "Images from BepInEx/config/ShipwrightsTouch/sails";
        }

        // A texture from the server or another player.
        private static string CatalogHint(CatalogEntry entry)
        {
            if (entry.Source == TextureSource.Server) return "From the server: everyone sees it.";
            switch (entry.Status)
            {
                case TextureStatus.Pending: return $"Shared by {entry.UploaderName}, waiting for approval: only moderators see it for now.";
                case TextureStatus.Denied: return $"Shared by {entry.UploaderName}, denied on this server.";
                default: return $"Shared by {entry.UploaderName}: everyone sees it.";
            }
        }

        // Who sees one of the player's own textures on this server, or what Apply will do with it.
        private static string SharingHint(string hash)
        {
            CatalogEntry entry = SailDownloads.Find(hash);
            if (entry != null && entry.Source == TextureSource.Server) return "The server has this texture too: everyone sees it.";
            if (entry != null)
            {
                switch (entry.Status)
                {
                    case TextureStatus.Pending: return "Waiting for approval: until then only you see it.";
                    case TextureStatus.Denied:
                        return entry.AutoDenied
                            ? "Denied automatically: unusual data in the file. Ask a moderator to review it."
                            : "This texture was denied on this server: only you see it.";
                    default: return "Shared: everyone sees this texture.";
                }
            }

            if (SailUploads.IsUploading(hash)) return "Sharing it with the server...";
            string refusal = SailUploads.RefusalOf(hash);
            if (refusal != null) return $"Not shared: {refusal}. Only you see it.";

            SailPolicy policy = SailNetwork.Policy;
            if (!policy.AllowPlayerTextures) return "This server doesn't take player textures: only you see this one.";
            if (!Plugin.ShareTextures.Value) return "Sharing is off in your settings: only you see this texture.";

            int shared = SailDownloads.SharedCount();
            if (shared >= policy.MaxTexturesPerPlayer) return $"Sharing {shared} of {policy.MaxTexturesPerPlayer}: this one stays visible only to you.";
            string problem = SailUploads.LocalProblem(hash);
            if (problem != null) return $"Won't be shared: {problem}. Only you'll see it.";
            return policy.RequireApproval
                ? $"Apply shares it ({shared} of {policy.MaxTexturesPerPlayer} used); a moderator approves it first."
                : $"Apply shares it with everyone ({shared} of {policy.MaxTexturesPerPlayer} used).";
        }

        private void AddTextureRow(
            string hash,
            string label,
            Texture thumbnail,
            bool usable = true)
        {
            var go = new GameObject($"Texture_{label}", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            go.transform.SetParent(_textureList, false);
            go.GetComponent<LayoutElement>().preferredHeight = RowHeight;

            var background = go.GetComponent<Image>();
            var button = go.GetComponent<Button>();
            button.targetGraphic = background;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            ColorBlock colors = button.colors;
            colors.normalColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            colors.highlightedColor = Color.white;
            colors.pressedColor = new Color(0.65f, 0.65f, 0.65f, 1f);
            colors.selectedColor = colors.normalColor;
            button.colors = colors;
            button.onClick.AddListener(() => SelectTexture(hash));
            button.interactable = usable;

            // Always created, so a thumbnail that arrives later can fill it in.
            RectTransform thumbRt = UIBuilder.MakeChildRect(go.transform, "Thumbnail");
            thumbRt.anchorMin = new Vector2(0f, 0.5f);
            thumbRt.anchorMax = new Vector2(0f, 0.5f);
            thumbRt.pivot = new Vector2(0f, 0.5f);
            thumbRt.sizeDelta = new Vector2(RowHeight - 8f, RowHeight - 8f);
            thumbRt.anchoredPosition = new Vector2(4f, 0f);
            var image = thumbRt.gameObject.AddComponent<RawImage>();
            image.texture = thumbnail;
            image.raycastTarget = false;
            image.enabled = thumbnail != null;

            RectTransform labelRt = UIBuilder.MakeChildRect(go.transform, "Label");
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = new Vector2(RowHeight + 8f, 0f);
            labelRt.offsetMax = new Vector2(-40f, 0f);
            var text = UIBuilder.AddText(labelRt, label, VanillaUI.BodyFont, 18f, TextAlignmentOptions.MidlineLeft);
            text.raycastTarget = false;
            text.richText = true;

            // Status icon slot (approved / pending / denied), filled once the server catalog exists.
            RectTransform statusRt = UIBuilder.MakeChildRect(go.transform, "Status");
            statusRt.anchorMin = new Vector2(1f, 0.5f);
            statusRt.anchorMax = new Vector2(1f, 0.5f);
            statusRt.pivot = new Vector2(1f, 0.5f);
            statusRt.sizeDelta = new Vector2(24f, 24f);
            statusRt.anchoredPosition = new Vector2(-8f, 0f);
            var status = statusRt.gameObject.AddComponent<Image>();
            status.raycastTarget = false;
            status.enabled = false;

            _textureRows.Add(new TextureRow { Hash = hash, Background = background, Thumbnail = image });
        }

        private void SelectTexture(string hash)
        {
            _textureHash = hash;
            RefreshTextureSelection();
            ColoringPatches.UpdateSailAppearance(Ship);
        }

        private void RefreshTextureSelection()
        {
            foreach (TextureRow row in _textureRows)
            {
                row.Background.color = row.Hash == _textureHash ? RowSelectedColor : RowColor;
            }
            _textureHint.text = TextureHint();
        }

        // ── Colors ───────────────────────────────────────────────────────

        private Swatch AddSwatch(RectTransform rt, Color color)
        {
            var image = rt.gameObject.AddComponent<Image>();
            image.color = color;

            var button = rt.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.transition = Selectable.Transition.None;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.onClick.AddListener(() => SelectColor(color));

            var outline = rt.gameObject.AddComponent<Outline>();
            outline.effectColor = UIPalette.AccentGold;
            outline.effectDistance = new Vector2(3f, 3f);
            outline.enabled = false;

            return new Swatch { Color = color, Outline = outline };
        }

        // The saved recent colors, led by the color being edited when it's a custom one not
        // saved yet, so a typed color shows up here before Apply. Only rebuilt when that list
        // changes, since the hex field calls this on every keystroke.
        private void RebuildRecentSwatches()
        {
            var recent = new List<Color>(SailStyle.RecentColors);
            string current = SailStyle.ToHex(_color);
            if (SailStyle.PresetIndexOf(_color) < 0 && !recent.Exists(c => SailStyle.ToHex(c) == current))
            {
                recent.Insert(0, _color);
                if (recent.Count > SailStyle.MaxRecentColors) recent.RemoveAt(recent.Count - 1);
            }

            string key = string.Join(",", recent.ConvertAll(SailStyle.ToHex));
            if (key == _recentKey) return;
            _recentKey = key;

            foreach (Transform child in _recentRow) Destroy(child.gameObject);
            _recentSwatches.Clear();

            for (int i = 0; i < recent.Count; i++)
            {
                RectTransform rt = UIBuilder.MakeChildRect(_recentRow, $"Recent_{i}");
                rt.anchorMin = new Vector2(0f, 0.5f);
                rt.anchorMax = new Vector2(0f, 0.5f);
                rt.pivot = new Vector2(0f, 0.5f);
                rt.sizeDelta = new Vector2(RecentSwatchSize, RecentSwatchSize);
                rt.anchoredPosition = new Vector2(i * (RecentSwatchSize + 6f), 0f);
                _recentSwatches.Add(AddSwatch(rt, recent[i]));
            }
        }

        private void SelectColor(Color color)
        {
            _color = color;
            RefreshColor();
            ColoringPatches.UpdateSailAppearance(Ship);
        }

        private void HexChanged(string text)
        {
            if (!SailStyle.TryParseHex(text, out Color color)) return;
            _color = color;
            RefreshSwatches();
            ColoringPatches.UpdateSailAppearance(Ship);
        }

        private void RefreshColor()
        {
            if (_hexField != null) _hexField.SetTextWithoutNotify(SailStyle.ToHex(_color));
            RefreshSwatches();
        }

        private void RefreshSwatches()
        {
            RebuildRecentSwatches();
            string selected = SailStyle.ToHex(_color);
            foreach (Swatch swatch in _presetSwatches) swatch.Outline.enabled = SailStyle.ToHex(swatch.Color) == selected;
            foreach (Swatch swatch in _recentSwatches) swatch.Outline.enabled = SailStyle.ToHex(swatch.Color) == selected;
        }

        // ── Layout helpers ───────────────────────────────────────────────

        // A full-width row (inside the margins), `top` px below the panel's top edge.
        private RectTransform Place(string name, float top, float height)
        {
            RectTransform rt = UIBuilder.MakeChildRect(transform, name);
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(Margin, -(top + height));
            rt.offsetMax = new Vector2(-Margin, -top);
            return rt;
        }

        private RectTransform PlaceBoxRect(string name, float left, float top, float width, float height)
        {
            RectTransform rt = UIBuilder.MakeChildRect(transform, name);
            PlaceBox(rt, left, top, width, height);
            return rt;
        }

        // A fixed-size box at (`left`, `top`) from the panel's top-left corner.
        private static void PlaceBox(RectTransform rt, float left, float top, float width, float height)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(width, height);
            rt.anchoredPosition = new Vector2(left, -top);
        }

        private static void PlaceFooterButton(RectTransform rt, bool left)
        {
            const float width = 150f;
            const float height = 44f;
            rt.anchorMin = new Vector2(left ? 0f : 1f, 0f);
            rt.anchorMax = rt.anchorMin;
            rt.pivot = new Vector2(left ? 0f : 1f, 0f);
            rt.sizeDelta = new Vector2(width, height);
            rt.anchoredPosition = new Vector2(left ? Margin : -Margin, 20f);
        }

        private void AddLabel(string name, string text, float top, float height, float fontSize = 18f)
        {
            RectTransform rt = PlaceBoxRect(name, Margin, top, LabelWidth, height);
            UIBuilder.AddText(rt, text, VanillaUI.BodyFont, fontSize, TextAlignmentOptions.MidlineLeft);
        }

        private void AddHeader(string name, string text, float top)
        {
            var header = UIBuilder.AddText(Place(name, top, 28f), UIPalette.Header(text), VanillaUI.BodyFont, 20f, TextAlignmentOptions.MidlineLeft);
            header.richText = true;
        }
    }
}
