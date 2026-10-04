using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using malafein.Valheim.Shared;
using malafein.Valheim.SharedUI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace malafein.Valheim.ShipwrightsTouch
{
    // The moderation panel: every texture players have shared on this server, pending ones first,
    // with a full-size preview, who shared it, who last decided on it, and how many ships use it.
    // Moderators and admins only; the server checks the permission again on every message.
    public static class ModerationPanel
    {
        private static ModerationPanelController s_controller;

        // See CustomizePanel.s_closedFrame.
        private static int s_closedFrame = -1;

        public static bool IsOpen => (s_controller != null && s_controller.IsOpen) || Time.frameCount == s_closedFrame;

        // Seconds to wait for the server's answer before refusing.
        private const float PermissionWaitSeconds = 3f;

        // Set while waiting to hear from the server whether this player may moderate.
        private static bool s_waiting;
        private static string s_waitingHash;
        private static float s_waitingSince;

        // Opens with a texture selected when given one (from a ship). On a modded server the
        // permissions this client knows may be out of date (the server's list files were edited),
        // so it asks the server first and opens or refuses on the answer. A hosting player is the
        // server and knows its own permissions.
        public static void Open(string selectHash = null)
        {
            if (s_controller == null)
            {
                Log.Warn("Moderation panel isn't built yet (Hud not awake).");
                return;
            }
            if (SailNetwork.Mode != ServerMode.Modded)
            {
                if (SailModeration.CanModerate) s_controller.Open(selectHash);
                else ShowRefusal();
                return;
            }
            if (s_waiting) return;

            s_waiting = true;
            s_waitingHash = selectHash;
            s_waitingSince = Time.time;
            SailNetwork.RequestRefresh();
        }

        internal static void ShowRefusal()
        {
            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, "Only this server's moderators and admins can moderate sail textures.");
        }

        // Every Policy from the server raises Changed, the answer to RequestRefresh included.
        private static void NetworkChanged()
        {
            if (!s_waiting) return;
            s_waiting = false;
            if (SailModeration.CanModerate && s_controller != null) s_controller.Open(s_waitingHash);
            else ShowRefusal();
        }

        private static bool IsOpenNow() => IsOpen;

        internal static void MarkClosed() => s_closedFrame = Time.frameCount;

        [HarmonyPatch]
        private static class Patches
        {
            [HarmonyPatch(typeof(Hud), "Awake")]
            [HarmonyPostfix]
            private static void Postfix_HudAwake(Hud __instance)
            {
                if (__instance.m_rootObject == null) return;
                s_controller = ModerationPanelController.Build(__instance.m_rootObject.transform);
                ModalPanels.Register(IsOpenNow);
                s_waiting = false;
                SailNetwork.Changed -= NetworkChanged;
                SailNetwork.Changed += NetworkChanged;
            }

            [HarmonyPatch(typeof(Player), "Update")]
            [HarmonyPostfix]
            private static void Postfix_PlayerUpdate(Player __instance)
            {
                if (__instance != Player.m_localPlayer) return;
                if (s_waiting && Time.time - s_waitingSince > PermissionWaitSeconds)
                {
                    s_waiting = false;
                    ShowRefusal();
                }
                if (TextInput.IsVisible() || IsOpen || CustomizePanel.IsOpen) return;
                if (!Keybinds.IsDown(Plugin.ModerationKey.Value) || !Keybinds.CanTakeInput(__instance)) return;
                Open();
            }
        }
    }

    public class ModerationPanelController : MonoBehaviour
    {
        private const float PanelWidth = 820f;
        private const float PanelHeight = 640f;
        private const float Margin = 24f;
        private const float ListWidth = 360f;
        private const float RowHeight = 52f;
        private const float PreviewSize = 256f;

        private static readonly Color RowColor = new Color(0.3f, 0.3f, 0.3f, 0.35f);
        private static readonly Color RowSelectedColor = new Color(1f, 0.718f, 0.36f, 0.45f);
        private static readonly Color HintColor = new Color(0.75f, 0.75f, 0.75f, 1f);

        private class Row
        {
            public string Hash;
            public Image Background;
            public RawImage Thumbnail;
        }

        public bool IsOpen => gameObject.activeSelf;

        private RectTransform _list;
        private ScrollRect _scroll;
        private TextMeshProUGUI _emptyText;
        private RawImage _preview;
        private TextMeshProUGUI _previewNote;
        private TextMeshProUGUI _details;
        private Button _approve;
        private Button _deny;
        private Button _remove;
        private TextMeshProUGUI _removeLabel;

        private readonly List<Row> _rows = new List<Row>();
        private string _selected;
        private string _listKey;
        private bool _confirmRemove;

        // Full-size previews loaded this session, by hash; destroyed on close.
        private readonly Dictionary<string, Texture2D> _previews = new Dictionary<string, Texture2D>();

        // ── Build ────────────────────────────────────────────────────────

        public static ModerationPanelController Build(Transform parent)
        {
            var go = new GameObject("ShipwrightsTouch_ModerationPanel", typeof(RectTransform), typeof(Image), typeof(ModerationPanelController));
            go.transform.SetParent(parent, false);

            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(PanelWidth, PanelHeight);
            rt.anchoredPosition = Vector2.zero;

            var background = go.GetComponent<Image>();
            if (!VanillaUI.ApplyPanelBackground(background))
                background.color = new Color(0.05f, 0.05f, 0.05f, 0.95f);

            // See CustomizePanelController.Build.
            go.AddComponent<Canvas>();
            go.AddComponent<GraphicRaycaster>();

            var controller = go.GetComponent<ModerationPanelController>();
            controller.BuildLayout();
            SailModeration.Changed += controller.ListChanged;
            SailDownloads.Changed += controller.DownloadsChanged;
            go.SetActive(false);
            return controller;
        }

        private void BuildLayout()
        {
            TMP_FontAsset font = VanillaUI.BodyFont;

            var title = UIBuilder.AddText(PlaceBoxRect("Title", Margin, 14f, PanelWidth - 2 * Margin, 44f), "Sail Moderation", font, 30f, TextAlignmentOptions.Center);
            VanillaUI.ApplyTitleStyle(title);

            // List of shared textures
            float listTop = 70f;
            float listHeight = PanelHeight - listTop - 84f;
            RectTransform listBox = PlaceBoxRect("ListBox", Margin, listTop, ListWidth, listHeight);
            var listBackground = listBox.gameObject.AddComponent<Image>();
            listBackground.color = new Color(0f, 0f, 0f, 0.3f);
            listBackground.raycastTarget = false;
            _list = UIBuilder.BuildScrollableList(listBox, "List", out GameObject listRoot);
            _scroll = listRoot.GetComponent<ScrollRect>();

            _emptyText = UIBuilder.AddText(PlaceBoxRect("Empty", Margin + 12f, listTop + 12f, ListWidth - 24f, 60f), "No textures shared by players yet.", font, 16f, TextAlignmentOptions.TopLeft);
            _emptyText.color = HintColor;

            // Preview and details of the selected texture
            float right = Margin + ListWidth + 24f;
            float rightWidth = PanelWidth - right - Margin;

            RectTransform previewBox = PlaceBoxRect("PreviewBox", right + (rightWidth - PreviewSize) / 2f, listTop, PreviewSize, PreviewSize);
            var previewBackground = previewBox.gameObject.AddComponent<Image>();
            previewBackground.color = new Color(0f, 0f, 0f, 0.3f);
            previewBackground.raycastTarget = false;

            RectTransform previewRt = UIBuilder.MakeChildRect(previewBox, "Preview");
            UIBuilder.Stretch(previewRt);
            _preview = previewRt.gameObject.AddComponent<RawImage>();
            _preview.raycastTarget = false;
            var fitter = previewRt.gameObject.AddComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;

            RectTransform noteRt = UIBuilder.MakeChildRect(previewBox, "PreviewNote");
            UIBuilder.Stretch(noteRt, 12f);
            _previewNote = UIBuilder.AddText(noteRt, "", font, 16f, TextAlignmentOptions.Center);
            _previewNote.color = HintColor;

            float detailsTop = listTop + PreviewSize + 12f;
            float buttonsTop = listTop + listHeight - 40f;
            _details = UIBuilder.AddText(PlaceBoxRect("Details", right, detailsTop, rightWidth, buttonsTop - detailsTop - 8f), "", font, 16f, TextAlignmentOptions.TopLeft);
            _details.richText = true;
            _details.textWrappingMode = TextWrappingModes.Normal;

            float buttonWidth = (rightWidth - 2 * 10f) / 3f;
            _approve = UIBuilder.AddButton(transform, "Approve", "Approve", () => Decide(ModerationAction.Approve), 16f);
            PlaceBox((RectTransform)_approve.transform, right, buttonsTop, buttonWidth, 40f);
            _deny = UIBuilder.AddButton(transform, "Deny", "Deny", () => Decide(ModerationAction.Deny), 16f);
            PlaceBox((RectTransform)_deny.transform, right + buttonWidth + 10f, buttonsTop, buttonWidth, 40f);
            _remove = UIBuilder.AddButton(transform, "Remove", "Remove", RemoveClicked, 16f);
            PlaceBox((RectTransform)_remove.transform, right + 2 * (buttonWidth + 10f), buttonsTop, buttonWidth, 40f);
            _removeLabel = _remove.GetComponentInChildren<TextMeshProUGUI>();

            Button close = UIBuilder.AddButton(transform, "Close", "Close", Close);
            var closeRt = (RectTransform)close.transform;
            closeRt.anchorMin = new Vector2(0.5f, 0f);
            closeRt.anchorMax = closeRt.anchorMin;
            closeRt.pivot = new Vector2(0.5f, 0f);
            closeRt.sizeDelta = new Vector2(150f, 44f);
            closeRt.anchoredPosition = new Vector2(0f, 20f);
        }

        // ── Open / Close ─────────────────────────────────────────────────

        public void Open(string selectHash)
        {
            _selected = selectHash;
            _confirmRemove = false;
            _scroll.scrollSensitivity = Plugin.ScrollSensitivity.Value;

            // Shows the last list right away; the fresh one replaces it when it arrives.
            _listKey = null;
            RebuildRows();
            SailModeration.RequestList();

            gameObject.SetActive(true);
            DrawAboveHud();
        }

        private void DrawAboveHud()
        {
            var canvas = GetComponent<Canvas>();
            Canvas parentCanvas = transform.parent != null ? transform.parent.GetComponentInParent<Canvas>() : null;
            canvas.overrideSorting = true;
            canvas.sortingOrder = (parentCanvas != null ? parentCanvas.sortingOrder : 0) + 10;
        }

        public void Close()
        {
            gameObject.SetActive(false);
            ModerationPanel.MarkClosed();
            _preview.texture = null;
            foreach (Texture2D texture in _previews.Values) Destroy(texture);
            _previews.Clear();
        }

        private void Update()
        {
            if (ZInput.GetKeyDown(KeyCode.Escape, false))
            {
                Close();
            }
            else if (!SailModeration.CanModerate)
            {
                // A later answer from the server took the permission away while the panel was open.
                Close();
                ModerationPanel.ShowRefusal();
            }
        }

        private void OnDestroy()
        {
            SailModeration.Changed -= ListChanged;
            SailDownloads.Changed -= DownloadsChanged;
            foreach (Texture2D texture in _previews.Values) Destroy(texture);
        }

        // ── List ─────────────────────────────────────────────────────────

        private void ListChanged()
        {
            if (IsOpen) RebuildRows();
        }

        // A thumbnail or full image arrived.
        private void DownloadsChanged()
        {
            if (!IsOpen) return;
            foreach (Row row in _rows)
            {
                if (row.Thumbnail.texture != null) continue;
                Texture2D thumbnail = SailDownloads.Thumbnail(row.Hash);
                if (thumbnail == null) continue;
                row.Thumbnail.texture = thumbnail;
                row.Thumbnail.enabled = true;
            }
            if (_preview.texture == null) RefreshDetails();
        }

        private void RebuildRows()
        {
            // Only when something changed, so the list doesn't jump on every resend.
            var key = new List<string>();
            foreach (ModerationEntry entry in SailModeration.Entries) key.Add($"{entry.Hash}{entry.Status}{entry.ShipsUsing}{entry.DecidedBy}");
            string listKey = string.Join(",", key);
            if (listKey == _listKey)
            {
                RefreshDetails();
                return;
            }
            _listKey = listKey;

            foreach (Transform child in _list) Destroy(child.gameObject);
            _rows.Clear();

            var missing = new List<string>();
            foreach (ModerationEntry entry in SailModeration.Entries)
            {
                Texture2D thumbnail = SailDownloads.Thumbnail(entry.Hash);
                if (thumbnail == null && entry.HasImage) missing.Add(entry.Hash);
                AddRow(entry, thumbnail);
            }
            SailDownloads.RequestThumbnails(missing);

            // A texture asked for from a ship stays selected even if the list doesn't have it, so
            // the details can say so.
            _emptyText.gameObject.SetActive(_rows.Count == 0);
            if (_selected == null && _rows.Count > 0) _selected = _rows[0].Hash;
            RefreshSelection();
        }

        private void AddRow(ModerationEntry entry, Texture thumbnail)
        {
            var go = new GameObject($"Texture_{entry.Name}", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            go.transform.SetParent(_list, false);
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
            string hash = entry.Hash;
            button.onClick.AddListener(() => Select(hash));

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
            labelRt.offsetMax = new Vector2(-8f, 0f);
            string label = entry.Name + Tag($"by {entry.UploaderName}", "#B0B0B0") + StatusTag(entry.Status);
            var text = UIBuilder.AddText(labelRt, label, VanillaUI.BodyFont, 18f, TextAlignmentOptions.MidlineLeft);
            text.raycastTarget = false;
            text.richText = true;
            text.enableAutoSizing = true;
            text.fontSizeMin = 12f;
            text.fontSizeMax = 18f;

            _rows.Add(new Row { Hash = hash, Background = background, Thumbnail = image });
        }

        private static string StatusTag(TextureStatus status)
        {
            switch (status)
            {
                case TextureStatus.Pending: return Tag("pending", "#E8C547");
                case TextureStatus.Denied: return Tag("denied", "#E06A5A");
                default: return Tag("approved", "#8FC97A");
            }
        }

        private static string Tag(string text, string color) => $" <size=75%><color={color}>{text}</color></size>";

        private void Select(string hash)
        {
            _selected = hash;
            _confirmRemove = false;
            RefreshSelection();
        }

        private void RefreshSelection()
        {
            foreach (Row row in _rows) row.Background.color = row.Hash == _selected ? RowSelectedColor : RowColor;
            RefreshDetails();
        }

        // ── Details ──────────────────────────────────────────────────────

        private void RefreshDetails()
        {
            ModerationEntry entry = SailModeration.Find(_selected);
            _approve.gameObject.SetActive(entry != null);
            _deny.gameObject.SetActive(entry != null);
            _remove.gameObject.SetActive(entry != null);
            if (entry == null)
            {
                _preview.texture = null;
                _preview.enabled = false;
                _previewNote.text = "";
                _details.text = _selected != null
                    ? "This texture isn't on the server's list: the server's own, never shared, or removed."
                    : "";
                return;
            }

            Texture2D preview = Preview(entry);
            _preview.texture = preview;
            _preview.enabled = preview != null;
            if (preview != null) _preview.GetComponent<AspectRatioFitter>().aspectRatio = (float)preview.width / preview.height;
            _previewNote.text = preview != null ? "" : entry.HasImage ? "Loading..." : "The server no longer has this image.";

            _details.text = Details(entry);

            _approve.interactable = entry.Status != TextureStatus.Approved && entry.HasImage;
            _deny.interactable = entry.Status != TextureStatus.Denied;
            _removeLabel.text = _confirmRemove ? "Confirm" : "Remove";
        }

        private string Details(ModerationEntry entry)
        {
            var lines = new List<string>
            {
                UIPalette.Header(entry.Name),
                $"Shared by {entry.UploaderName} on {entry.Date}",
                $"{entry.Width}x{entry.Height}, {(entry.Bytes + 1023) / 1024} KB",
                entry.ShipsUsing == 1 ? "Used on 1 ship" : $"Used on {entry.ShipsUsing} ships"
            };

            string decided = entry.DecidedBy != "" ? $" by {entry.DecidedBy}, {entry.DecidedDate}" : "";
            switch (entry.Status)
            {
                case TextureStatus.Pending: lines.Add($"<color=#E8C547>Pending</color>: only its uploader and moderators see it."); break;
                case TextureStatus.Denied: lines.Add($"<color=#E06A5A>Denied</color>{decided}: only its uploader sees it."); break;
                default: lines.Add(entry.DecidedBy != "" ? $"<color=#8FC97A>Approved</color>{decided}" : "<color=#8FC97A>Approved</color> (no approval needed when shared)"); break;
            }

            if (_confirmRemove)
            {
                string ships = entry.ShipsUsing > 0 ? $" {entry.ShipsUsing} ship(s) using it go back to the vanilla sail for everyone but their owners." : "";
                lines.Add($"<color=#E06A5A>Remove deletes it from the server and frees {entry.UploaderName}'s slot; it may be shared again.{ships} Click Confirm to remove.</color>");
            }
            return string.Join("\n", lines);
        }

        // The full image: read from the server's own files when hosting, otherwise downloaded
        // into the client cache once and loaded from there. Null until it's here.
        private Texture2D Preview(ModerationEntry entry)
        {
            if (!entry.HasImage) return null;
            if (_previews.TryGetValue(entry.Hash, out Texture2D texture)) return texture;

            string path = SailServer.ModerationPathOf(entry.Hash);
            if (path == null && SailDownloads.IsCached(entry.Hash)) path = SailDownloads.CachedPath(entry.Hash);
            if (path == null)
            {
                SailDownloads.Request(entry.Hash, forModeration: true);
                return null;
            }

            texture = new Texture2D(2, 2);
            try
            {
                if (!texture.LoadImage(File.ReadAllBytes(path)))
                {
                    Destroy(texture);
                    return null;
                }
            }
            catch (IOException e)
            {
                Log.Warn($"Could not read sail texture {path}: {e.Message}");
                Destroy(texture);
                return null;
            }
            _previews[entry.Hash] = texture;
            return texture;
        }

        // ── Actions ──────────────────────────────────────────────────────

        private void Decide(ModerationAction action)
        {
            if (SailModeration.Find(_selected) == null) return;
            _confirmRemove = false;
            SailModeration.Send(_selected, action);

            // The next list won't have it; select the first texture then.
            if (action == ModerationAction.Remove) _selected = null;
        }

        private void RemoveClicked()
        {
            if (!_confirmRemove)
            {
                _confirmRemove = true;
                RefreshDetails();
                return;
            }
            Decide(ModerationAction.Remove);
        }

        // ── Layout helpers ───────────────────────────────────────────────

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
    }
}
