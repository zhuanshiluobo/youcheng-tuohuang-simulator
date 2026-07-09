using System;
using System.Collections.Generic;
using YC.Application.DevTools;
using YC.Application.Sessions;
using YC.Domain.Rules;
using YC.Infrastructure.Multiplayer;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class StartMenuController : MonoBehaviour
    {
        private static readonly Vector2 CoverReferenceSize = new Vector2(5888f, 3312f);
        private static readonly Rect ButtonImageRect = new Rect(2220f, 2528f, 1460f, 323f);
        private const string DevStartLocalhostArg = "--yc-dev-start-localhost";
        private const string OfficialSiteUrl = "https://ak.hypergryph.com/boardgame_nomadcity";
        private const string WikiUrl = "https://prts.wiki/w/%E6%B8%B8%E5%9F%8E%E6%8B%93%E8%8D%92%EF%BC%9A%E9%93%B8%E5%9F%BA%E8%80%85";
        private static Sprite bookmarkSprite;

        [SerializeField] private string mapSceneName = "SampleScene";
        [SerializeField] private Texture2D coverTexture;

        private readonly UnityOfficialRoomService roomService = new UnityOfficialRoomService();
        private readonly object networkEventLock = new object();
        private RectTransform coverFrame;
        private GameObject roomPanel;
        private InputField joinRoomInput;
        private Text roomStatusText;
        private RoomState pendingRoomUpdate;
        private RoomState pendingGameStart;
        private string pendingNetworkError;
        private bool pendingRoomDisbanded;
        private int selectedRoomPlayerCount = 4;
        private bool loadingGame;
        private bool joiningRoom;

        private void Awake()
        {
            UnityEngine.Application.runInBackground = true;
            if (TryRunDevCommandLineTask())
            {
                return;
            }

            if (ShouldStartLocalhostFromCommandLine())
            {
                StartGame();
                return;
            }

            roomService.RoomUpdated += QueueRoomUpdate;
            roomService.GameStarted += QueueGameStart;
            roomService.RoomDisbanded += QueueRoomDisbanded;
            roomService.ErrorOccurred += QueueNetworkError;
            BuildMenu();
        }

        private static bool ShouldStartLocalhostFromCommandLine()
        {
            return HasCommandLineArg(Environment.GetCommandLineArgs(), DevStartLocalhostArg);
        }

        private static bool TryRunDevCommandLineTask()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var args = Environment.GetCommandLineArgs();
            if (HasCommandLineArg(args, "--yc-dev-font-health-check") ||
                HasCommandLineArg(args, "--yc-dev-font-health-check-simulate"))
            {
                var mode = HasCommandLineArg(args, "--yc-dev-font-health-check-simulate")
                    ? FontHealthCheckMode.SimulateRecreate
                    : FontHealthCheckMode.CheckOnly;
                try
                {
                    var result = FontHealthCheckRunner.Run(mode);
                    Debug.Log(result.Snapshot);
                    if (!UnityEngine.Application.isEditor && UnityEngine.Application.isBatchMode)
                    {
                        UnityEngine.Application.Quit(0);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    if (!UnityEngine.Application.isEditor && UnityEngine.Application.isBatchMode)
                    {
                        UnityEngine.Application.Quit(1);
                    }
                }

                return true;
            }

            for (var i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], "--yc-dev-autoplay-localhost", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var result = LocalhostAutoplayRunner.RunToRound8Settlement();
                if (result.Succeeded)
                {
                    Debug.Log(result.Snapshot);
                }
                else
                {
                    Debug.LogError(result.Snapshot);
                }

                if (!UnityEngine.Application.isEditor && UnityEngine.Application.isBatchMode)
                {
                    UnityEngine.Application.Quit(result.Succeeded ? 0 : 1);
                }

                return true;
            }
#endif
            return false;
        }

        private static bool HasCommandLineArg(string[] args, string expectedValue)
        {
            if (args == null)
            {
                return false;
            }

            for (var i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], expectedValue, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private void Update()
        {
            RoomState roomUpdate = null;
            RoomState gameStart = null;
            string networkError = null;
            var roomDisbanded = false;

            lock (networkEventLock)
            {
                roomUpdate = pendingRoomUpdate;
                gameStart = pendingGameStart;
                networkError = pendingNetworkError;
                roomDisbanded = pendingRoomDisbanded;
                pendingRoomUpdate = null;
                pendingGameStart = null;
                pendingNetworkError = null;
                pendingRoomDisbanded = false;
            }

            if (roomUpdate != null)
            {
                joiningRoom = false;
                ShowRoomPanel(roomUpdate, roomUpdate.LocalPlayerId == roomUpdate.HostPlayerId);
            }

            if (!string.IsNullOrEmpty(networkError))
            {
                joiningRoom = false;
                SetRoomStatus(networkError);
            }

            if (roomDisbanded)
            {
                joiningRoom = false;
                ShowRoomDisbandedPanel();
                return;
            }

            if (gameStart != null)
            {
                StartRoomGame(gameStart);
            }
        }

        private void OnDestroy()
        {
            roomService.RoomUpdated -= QueueRoomUpdate;
            roomService.GameStarted -= QueueGameStart;
            roomService.RoomDisbanded -= QueueRoomDisbanded;
            roomService.ErrorOccurred -= QueueNetworkError;

            if (!loadingGame)
            {
                roomService.Dispose();
            }
        }

        public void StartGame()
        {
            var seats = new List<PlayerSeat>
            {
                new PlayerSeat
                {
                    PlayerId = 1,
                    PlayerName = "Player 1",
                    Color = PlayerColor.Blue,
                    IsReady = true
                }
            };

            GameLaunchContext.Ensure().Configure(LaunchMode.Local, 1, string.Empty, seats);
            SceneManager.LoadScene(mapSceneName);
        }

        public async void CreateRoom()
        {
            if (joiningRoom)
            {
                return;
            }

            joiningRoom = true;
            ShowRoomProgressPanel("创建房间", "正在初始化 Unity Lobby / Relay...");

            try
            {
                var room = await roomService.CreateRoomAsync("Player 1", selectedRoomPlayerCount);
                joiningRoom = false;
                ShowRoomPanel(room, true);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogException(ex);
                joiningRoom = false;
                SetRoomStatus("创建房间失败：" + ex.Message);
            }
        }

        public void JoinRoom()
        {
            ShowJoinRoomPanel();
        }

        private void BuildMenu()
        {
            EnsureEventSystem();

            var canvasObject = new GameObject("Start Menu Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            coverFrame = CreateCoverFrame(canvasObject.GetComponent<RectTransform>());
            CreateCover(coverFrame);
            CreateExternalLinkButtons(coverFrame);
            CreateMenuButtons(coverFrame);
            GameSettingsMenuController.EnsureInScene(transform, false, false);
        }

        private static RectTransform CreateCoverFrame(RectTransform canvasTransform)
        {
            Canvas.ForceUpdateCanvases();

            var coverFrameObject = new GameObject("Cover Frame", typeof(RectTransform));
            coverFrameObject.transform.SetParent(canvasTransform, false);

            var coverFrame = coverFrameObject.GetComponent<RectTransform>();
            coverFrame.anchorMin = new Vector2(0.5f, 0.5f);
            coverFrame.anchorMax = new Vector2(0.5f, 0.5f);
            coverFrame.pivot = new Vector2(0.5f, 0.5f);

            var canvasSize = canvasTransform.rect.size;
            var frameWidth = canvasSize.x;
            var frameHeight = frameWidth * CoverReferenceSize.y / CoverReferenceSize.x;
            if (frameHeight > canvasSize.y)
            {
                frameHeight = canvasSize.y;
                frameWidth = frameHeight * CoverReferenceSize.x / CoverReferenceSize.y;
            }

            if (frameWidth <= 0f || frameHeight <= 0f)
            {
                frameWidth = 1920f;
                frameHeight = 1080f;
            }

            coverFrame.sizeDelta = new Vector2(frameWidth, frameHeight);
            coverFrame.anchoredPosition = Vector2.zero;
            return coverFrame;
        }

        private void CreateCover(Transform parent)
        {
            var coverObject = new GameObject("Rulebook Cover", typeof(RectTransform), typeof(RawImage));
            coverObject.transform.SetParent(parent, false);

            var rectTransform = coverObject.GetComponent<RectTransform>();
            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.one;
            rectTransform.offsetMin = Vector2.zero;
            rectTransform.offsetMax = Vector2.zero;

            var image = coverObject.GetComponent<RawImage>();
            image.texture = coverTexture;
            image.color = Color.white;
        }

        private void CreateMenuButtons(Transform parent)
        {
            CreateMenuButton(parent, "单机开始", 2, StartGame);
            CreateMenuButton(parent, "创建房间", 1, CreateRoom);
            CreateMenuButton(parent, "加入房间", 0, JoinRoom);
        }

        private void CreateExternalLinkButtons(RectTransform parent)
        {
            CreateExternalLinkButton(parent, "官方网站", OfficialSiteUrl, "官", 1);
            CreateExternalLinkButton(parent, "进入wiki", WikiUrl, "W", 0);
        }

        private static void CreateExternalLinkButton(RectTransform parent, string label, string url, string mark, int row)
        {
            const float iconWidth = 52f;
            const float iconHeight = 76f;
            const float expandedWidth = 210f;
            const float spacing = 12f;

            var buttonObject = new GameObject(label + " Link Button", typeof(RectTransform), typeof(Image), typeof(Button));
            buttonObject.transform.SetParent(parent, false);

            var rectTransform = buttonObject.GetComponent<RectTransform>();
            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.zero;
            rectTransform.pivot = new Vector2(0f, 0.5f);
            rectTransform.sizeDelta = new Vector2(expandedWidth, iconHeight);
            rectTransform.anchoredPosition = new Vector2(38f, 52f + row * (iconHeight + spacing));

            var hitArea = buttonObject.GetComponent<Image>();
            hitArea.color = new Color(1f, 1f, 1f, 0.001f);

            buttonObject.GetComponent<Button>().onClick.AddListener(() => UnityEngine.Application.OpenURL(url));

            var extensionObject = new GameObject("Hover Label", typeof(RectTransform), typeof(Image), typeof(Outline), typeof(CanvasGroup));
            extensionObject.transform.SetParent(buttonObject.transform, false);

            var extensionRect = extensionObject.GetComponent<RectTransform>();
            extensionRect.anchorMin = new Vector2(0f, 0.5f);
            extensionRect.anchorMax = new Vector2(0f, 0.5f);
            extensionRect.pivot = new Vector2(0f, 0.5f);
            extensionRect.sizeDelta = new Vector2(0f, 42f);
            extensionRect.anchoredPosition = new Vector2(iconWidth - 4f, 0f);

            var extensionImage = extensionObject.GetComponent<Image>();
            extensionImage.color = new Color(0.14f, 0.085f, 0.045f, 0.95f);
            extensionImage.raycastTarget = false;
            var extensionOutline = extensionObject.GetComponent<Outline>();
            extensionOutline.effectColor = new Color(0.78f, 0.63f, 0.38f, 0.85f);
            extensionOutline.effectDistance = new Vector2(2f, -2f);

            var labelObject = new GameObject("Text", typeof(RectTransform), typeof(Text), typeof(Outline), typeof(CanvasGroup));
            labelObject.transform.SetParent(extensionObject.transform, false);

            var labelRect = labelObject.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(18f, 0f);
            labelRect.offsetMax = new Vector2(-16f, 0f);

            var text = labelObject.GetComponent<Text>();
            text.text = label;
            text.alignment = TextAnchor.MiddleLeft;
            text.color = new Color(0.86f, 0.75f, 0.55f, 1f);
            text.fontSize = 22;
            text.fontStyle = FontStyle.Bold;
            text.font = FontUtility.GetCjkFont(text.fontSize);
            text.raycastTarget = false;

            var textOutline = labelObject.GetComponent<Outline>();
            textOutline.effectColor = new Color(0.06f, 0.04f, 0.025f, 0.96f);
            textOutline.effectDistance = new Vector2(2f, -2f);

            var labelCanvasGroup = labelObject.GetComponent<CanvasGroup>();
            labelCanvasGroup.alpha = 0f;

            var iconObject = new GameObject("Bookmark Icon", typeof(RectTransform), typeof(Image), typeof(Outline));
            iconObject.transform.SetParent(buttonObject.transform, false);

            var iconRect = iconObject.GetComponent<RectTransform>();
            iconRect.anchorMin = new Vector2(0f, 0.5f);
            iconRect.anchorMax = new Vector2(0f, 0.5f);
            iconRect.pivot = new Vector2(0.5f, 0.5f);
            iconRect.sizeDelta = new Vector2(iconWidth, iconHeight);
            iconRect.anchoredPosition = new Vector2(iconWidth * 0.5f, 0f);

            var iconImage = iconObject.GetComponent<Image>();
            iconImage.sprite = GetBookmarkSprite();
            iconImage.color = new Color(0.16f, 0.1f, 0.055f, 0.98f);
            iconImage.raycastTarget = false;

            var iconOutline = iconObject.GetComponent<Outline>();
            iconOutline.effectColor = new Color(0.78f, 0.63f, 0.38f, 0.9f);
            iconOutline.effectDistance = new Vector2(2f, -2f);

            var markerObject = new GameObject("Bookmark Mark", typeof(RectTransform), typeof(Text), typeof(Outline));
            markerObject.transform.SetParent(iconObject.transform, false);

            var markerRect = markerObject.GetComponent<RectTransform>();
            markerRect.anchorMin = new Vector2(0.5f, 0.5f);
            markerRect.anchorMax = new Vector2(0.5f, 0.5f);
            markerRect.pivot = new Vector2(0.5f, 0.5f);
            markerRect.sizeDelta = new Vector2(iconWidth, 46f);
            markerRect.anchoredPosition = new Vector2(0f, 8f);

            var marker = markerObject.GetComponent<Text>();
            marker.text = mark;
            marker.alignment = TextAnchor.MiddleCenter;
            marker.color = new Color(0.86f, 0.75f, 0.55f, 1f);
            marker.fontSize = 24;
            marker.fontStyle = FontStyle.Bold;
            marker.font = FontUtility.GetCjkFont(marker.fontSize);
            marker.horizontalOverflow = HorizontalWrapMode.Overflow;
            marker.verticalOverflow = VerticalWrapMode.Overflow;
            marker.alignByGeometry = true;
            marker.supportRichText = false;
            marker.raycastTarget = false;

            var markerOutline = markerObject.GetComponent<Outline>();
            markerOutline.effectColor = new Color(0.06f, 0.04f, 0.025f, 0.96f);
            markerOutline.effectDistance = new Vector2(1f, -1f);

            var hover = buttonObject.AddComponent<BookmarkLinkHover>();
            hover.Initialize(rectTransform, extensionRect, extensionObject.GetComponent<CanvasGroup>(), labelCanvasGroup, iconWidth, expandedWidth);
        }

        private static Sprite GetBookmarkSprite()
        {
            if (bookmarkSprite != null)
            {
                return bookmarkSprite;
            }

            const int width = 64;
            const int height = 96;
            const int notchHeight = 22;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;

            var center = (width - 1) * 0.5f;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var inside = true;
                    if (y < notchHeight)
                    {
                        var progress = y / (float)notchHeight;
                        var leftBoundary = Mathf.Lerp(0f, center, progress);
                        var rightBoundary = Mathf.Lerp(width - 1f, center, progress);
                        inside = x <= leftBoundary || x >= rightBoundary;
                    }

                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, inside ? 1f : 0f));
                }
            }

            texture.Apply();
            bookmarkSprite = Sprite.Create(texture, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f), height);
            return bookmarkSprite;
        }

        private void CreateMenuButton(Transform parent, string label, int row, UnityEngine.Events.UnityAction action)
        {
            var buttonObject = new GameObject(label + " Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            buttonObject.transform.SetParent(parent, false);

            var rectTransform = buttonObject.GetComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            rectTransform.pivot = new Vector2(0.5f, 0.5f);

            var parentTransform = (RectTransform)parent;
            var frameSize = parentTransform.sizeDelta.x > 0f && parentTransform.sizeDelta.y > 0f
                ? parentTransform.sizeDelta
                : new Vector2(1920f, 1080f);
            var scaleX = frameSize.x / CoverReferenceSize.x;
            var scaleY = frameSize.y / CoverReferenceSize.y;

            rectTransform.sizeDelta = new Vector2(ButtonImageRect.width * scaleX, ButtonImageRect.height * scaleY);
            var basePosition = new Vector2(
                (ButtonImageRect.center.x - CoverReferenceSize.x * 0.5f) * scaleX,
                (CoverReferenceSize.y * 0.5f - ButtonImageRect.center.y) * scaleY);
            rectTransform.anchoredPosition = basePosition + new Vector2(0f, row * (rectTransform.sizeDelta.y + 20f));

            var image = buttonObject.GetComponent<Image>();
            image.color = new Color(0.16f, 0.1f, 0.055f, 0.96f);

            var button = buttonObject.GetComponent<Button>();
            button.onClick.AddListener(action);

            var buttonOutline = buttonObject.GetComponent<Outline>();
            buttonOutline.effectColor = new Color(0.78f, 0.63f, 0.38f, 0.9f);
            buttonOutline.effectDistance = new Vector2(4f, -4f);

            var textObject = new GameObject("Text", typeof(RectTransform), typeof(Text), typeof(Outline));
            textObject.transform.SetParent(buttonObject.transform, false);

            var textRect = textObject.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            var text = textObject.GetComponent<Text>();
            text.text = label;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = new Color(0.86f, 0.75f, 0.55f, 1f);
            text.fontSize = Mathf.RoundToInt(100f * scaleY);
            text.fontStyle = FontStyle.Bold;
            text.font = FontUtility.GetCjkFont(text.fontSize);

            var outline = textObject.GetComponent<Outline>();
            outline.effectColor = new Color(0.06f, 0.04f, 0.025f, 0.98f);
            outline.effectDistance = new Vector2(4f, -4f);
        }

        private void ShowJoinRoomPanel()
        {
            if (roomPanel != null)
            {
                Destroy(roomPanel);
            }

            roomPanel = CreatePanel("Join Room Panel", new Vector2(620f, 310f));
            var rect = roomPanel.GetComponent<RectTransform>();

            CreatePanelText(rect, "加入房间", 28, new Vector2(0f, 105f), FontStyle.Bold);
            CreatePanelText(rect, "输入房主显示的 Lobby 房间码", 18, new Vector2(0f, 62f), FontStyle.Normal);

            var inputObject = new GameObject("Room Code Input", typeof(RectTransform), typeof(Image), typeof(InputField), typeof(Outline));
            inputObject.transform.SetParent(rect, false);

            var inputRect = inputObject.GetComponent<RectTransform>();
            inputRect.anchorMin = new Vector2(0.5f, 0.5f);
            inputRect.anchorMax = new Vector2(0.5f, 0.5f);
            inputRect.sizeDelta = new Vector2(340f, 46f);
            inputRect.anchoredPosition = new Vector2(-46f, 10f);

            inputObject.GetComponent<Image>().color = new Color(0.04f, 0.035f, 0.03f, 0.98f);
            inputObject.GetComponent<Outline>().effectColor = new Color(0.78f, 0.63f, 0.38f, 0.9f);

            var textObject = new GameObject("Text", typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(inputObject.transform, false);
            var textRect = textObject.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(12f, 0f);
            textRect.offsetMax = new Vector2(-12f, 0f);

            var text = textObject.GetComponent<Text>();
            text.alignment = TextAnchor.MiddleLeft;
            text.color = new Color(0.86f, 0.75f, 0.55f, 1f);
            text.fontSize = 20;
            text.font = FontUtility.GetCjkFont(text.fontSize);

            var placeholderObject = new GameObject("Placeholder", typeof(RectTransform), typeof(Text));
            placeholderObject.transform.SetParent(inputObject.transform, false);
            var placeholderRect = placeholderObject.GetComponent<RectTransform>();
            placeholderRect.anchorMin = Vector2.zero;
            placeholderRect.anchorMax = Vector2.one;
            placeholderRect.offsetMin = new Vector2(12f, 0f);
            placeholderRect.offsetMax = new Vector2(-12f, 0f);

            var placeholder = placeholderObject.GetComponent<Text>();
            placeholder.text = "例如 AB12CD";
            placeholder.alignment = TextAnchor.MiddleLeft;
            placeholder.color = new Color(0.55f, 0.48f, 0.36f, 0.9f);
            placeholder.fontSize = 20;
            placeholder.font = text.font;

            joinRoomInput = inputObject.GetComponent<InputField>();
            joinRoomInput.textComponent = text;
            joinRoomInput.placeholder = placeholder;

            CreateSmallButton(rect, "粘贴", new Vector2(218f, 10f), PasteRoomCodeFromClipboard);

            roomStatusText = CreatePanelText(rect, string.Empty, 16, new Vector2(0f, -38f), FontStyle.Normal);

            CreateSmallButton(rect, "加入", new Vector2(-70f, -105f), ConnectToRoom);
            CreateSmallButton(rect, "返回", new Vector2(70f, -105f), HideRoomPanel);
        }

        private async void ConnectToRoom()
        {
            if (joiningRoom)
            {
                return;
            }

            if (joinRoomInput == null || string.IsNullOrEmpty(joinRoomInput.text))
            {
                SetRoomStatus("请输入房间号。");
                return;
            }

            var roomCode = joinRoomInput.text.Trim();
            joiningRoom = true;
            SetRoomStatus("正在通过 Unity Lobby / Relay 加入房间...");

            try
            {
                var room = await roomService.JoinRoomAsync(roomCode, "Player 2");
                joiningRoom = false;
                ShowRoomPanel(room, false);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogException(ex);
                joiningRoom = false;
                SetRoomStatus("加入房间失败：" + ex.Message);
            }
        }

        private void ShowRoomPanel(RoomState room, bool hostControls)
        {
            if (room == null)
            {
                return;
            }

            if (roomPanel != null)
            {
                Destroy(roomPanel);
            }

            roomPanel = CreatePanel(hostControls ? "Host Room Panel" : "Client Room Panel", new Vector2(620f, 430f));
            var rect = roomPanel.GetComponent<RectTransform>();
            roomStatusText = null;

            var roomCodeText = CreatePanelText(rect, "房间号 " + room.RoomId, 28, new Vector2(-58f, 170f), FontStyle.Bold);
            roomCodeText.GetComponent<RectTransform>().sizeDelta = new Vector2(410f, 34f);
            CreateSmallButton(rect, "复制", new Vector2(235f, 170f), () => CopyRoomCodeToClipboard(room.RoomId));

            for (var i = 0; i < room.Seats.Count; i++)
            {
                var seat = room.Seats[i];
                var status = seat.IsReady ? "已加入" : "等待加入";
                var marker = seat.PlayerId == room.LocalPlayerId ? "（你）" : string.Empty;
                var line = string.Format("{0}. {1}{2}  {3}", seat.PlayerId, seat.PlayerName, marker, status);
                CreatePanelText(rect, line, 20, new Vector2(0f, 110f - i * 38f), FontStyle.Normal);
            }

            roomStatusText = CreatePanelText(
                rect,
                hostControls ? "等待玩家加入，房主可开始游戏。" : "已加入房间，等待房主开始。",
                16,
                new Vector2(0f, -66f),
                FontStyle.Normal);

            if (hostControls)
            {
                CreateSmallButton(rect, "开始", new Vector2(100f, -125f), roomService.StartGame);
                CreateSmallButton(rect, "返回", new Vector2(220f, -125f), HideRoomPanel);
            }
            else
            {
                CreateSmallButton(rect, "返回", new Vector2(0f, -125f), HideRoomPanel);
            }
        }

        private GameObject CreatePanel(string panelName, Vector2 size)
        {
            roomPanel = new GameObject("Room Panel", typeof(RectTransform), typeof(Image), typeof(Outline));
            roomPanel.transform.SetParent(coverFrame, false);

            var rect = roomPanel.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = Vector2.zero;

            roomPanel.GetComponent<Image>().color = new Color(0.08f, 0.07f, 0.06f, 0.96f);
            roomPanel.GetComponent<Outline>().effectColor = new Color(0.78f, 0.63f, 0.38f, 0.9f);
            roomPanel.name = panelName;
            return roomPanel;
        }

        private void ShowRoomProgressPanel(string title, string message)
        {
            if (roomPanel != null)
            {
                Destroy(roomPanel);
            }

            roomPanel = CreatePanel(title + " Panel", new Vector2(560f, 220f));
            var rect = roomPanel.GetComponent<RectTransform>();
            joinRoomInput = null;
            CreatePanelText(rect, title, 28, new Vector2(0f, 46f), FontStyle.Bold);
            roomStatusText = CreatePanelText(rect, message, 17, new Vector2(0f, 2f), FontStyle.Normal);
            CreateSmallButton(rect, "取消", new Vector2(0f, -70f), HideRoomPanel);
        }

        private void StartRoomGame(RoomState room)
        {
            if (room == null)
            {
                SetRoomStatus("房间状态不可用，请重新加入房间。");
                return;
            }

            loadingGame = true;
            var localPlayerId = room.LocalPlayerId;
            var manager = NetworkManager.Singleton;
            localPlayerId = GameLaunchStateFactory.ResolveHostLocalPlayerId(
                localPlayerId,
                room.HostPlayerId,
                manager != null && manager.IsHost,
                room.Seats);

            if (localPlayerId <= 0 || !RoomContainsPlayer(room, localPlayerId))
            {
                loadingGame = false;
                SetRoomStatus("无法确认本机玩家座位，请重新加入房间。");
                return;
            }

            var mode = localPlayerId == room.HostPlayerId ? LaunchMode.Host : LaunchMode.Client;
            GameLaunchContext.Ensure().Configure(mode, localPlayerId, room.RoomId, room.Seats);
            SceneManager.LoadScene(mapSceneName);
        }

        private static bool RoomContainsPlayer(RoomState room, int playerId)
        {
            if (room == null || room.Seats == null)
            {
                return false;
            }

            for (var i = 0; i < room.Seats.Count; i++)
            {
                if (room.Seats[i].PlayerId == playerId)
                {
                    return true;
                }
            }

            return false;
        }

        private void HideRoomPanel()
        {
            if (roomPanel != null)
            {
                Destroy(roomPanel);
                roomPanel = null;
            }

            joinRoomInput = null;
            roomStatusText = null;
            joiningRoom = false;
            roomService.Shutdown();
        }

        private void ShowRoomDisbandedPanel()
        {
            if (roomPanel != null)
            {
                Destroy(roomPanel);
            }

            roomPanel = CreatePanel("Room Disbanded Panel", new Vector2(520f, 220f));
            var rect = roomPanel.GetComponent<RectTransform>();
            roomStatusText = null;
            joinRoomInput = null;

            CreatePanelText(rect, "房间已解散", 28, new Vector2(0f, 54f), FontStyle.Bold);
            CreatePanelText(rect, "点击确认后返回主页", 18, new Vector2(0f, 12f), FontStyle.Normal);
            CreateSmallButton(rect, "确认", new Vector2(0f, -66f), ConfirmRoomDisbanded);
        }

        private void ConfirmRoomDisbanded()
        {
            if (roomPanel != null)
            {
                Destroy(roomPanel);
                roomPanel = null;
            }

            roomStatusText = null;
            joinRoomInput = null;
            joiningRoom = false;
            roomService.Shutdown();
        }

        private static Text CreatePanelText(RectTransform parent, string value, int size, Vector2 position, FontStyle style)
        {
            var textObject = new GameObject("Text", typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(parent, false);

            var rect = textObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(540f, 34f);
            rect.anchoredPosition = position;

            var text = textObject.GetComponent<Text>();
            text.text = value;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = new Color(0.86f, 0.75f, 0.55f, 1f);
            text.fontSize = size;
            text.fontStyle = style;
            text.font = FontUtility.GetCjkFont(size);
            return text;
        }

        private static void CreateSmallButton(RectTransform parent, string label, Vector2 position, UnityEngine.Events.UnityAction action)
        {
            var buttonObject = new GameObject(label + " Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            buttonObject.transform.SetParent(parent, false);

            var rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(96f, 44f);
            rect.anchoredPosition = position;

            buttonObject.GetComponent<Image>().color = new Color(0.16f, 0.1f, 0.055f, 0.96f);
            buttonObject.GetComponent<Button>().onClick.AddListener(action);
            buttonObject.GetComponent<Outline>().effectColor = new Color(0.78f, 0.63f, 0.38f, 0.9f);

            var textObject = new GameObject("Text", typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(buttonObject.transform, false);

            var textRect = textObject.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            var text = textObject.GetComponent<Text>();
            text.text = label;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = new Color(0.86f, 0.75f, 0.55f, 1f);
            text.fontSize = 20;
            text.fontStyle = FontStyle.Bold;
            text.font = FontUtility.GetCjkFont(text.fontSize);
        }

        private void SetRoomStatus(string message)
        {
            if (roomStatusText != null)
            {
                roomStatusText.text = message;
            }
        }

        private void CopyRoomCodeToClipboard(string roomCode)
        {
            if (string.IsNullOrEmpty(roomCode))
            {
                SetRoomStatus("房间号不可用，无法复制。");
                return;
            }

            GUIUtility.systemCopyBuffer = roomCode;
            SetRoomStatus("房间号已复制。");
        }

        private void PasteRoomCodeFromClipboard()
        {
            if (joinRoomInput == null)
            {
                return;
            }

            var roomCode = GUIUtility.systemCopyBuffer;
            if (string.IsNullOrEmpty(roomCode))
            {
                SetRoomStatus("剪贴板没有可粘贴的房间号。");
                return;
            }

            joinRoomInput.text = roomCode.Trim();
            joinRoomInput.ActivateInputField();
            SetRoomStatus("已粘贴房间号。");
        }

        private void QueueRoomUpdate(RoomState room)
        {
            lock (networkEventLock)
            {
                pendingRoomUpdate = room;
            }
        }

        private void QueueGameStart(RoomState room)
        {
            lock (networkEventLock)
            {
                pendingGameStart = room;
            }
        }

        private void QueueNetworkError(string message)
        {
            lock (networkEventLock)
            {
                pendingNetworkError = message;
            }
        }

        private void QueueRoomDisbanded()
        {
            lock (networkEventLock)
            {
                pendingRoomDisbanded = true;
            }
        }

        private sealed class BookmarkLinkHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
        {
            private const float AnimationSpeed = 14f;

            private RectTransform buttonRect;
            private RectTransform labelRect;
            private CanvasGroup extensionGroup;
            private CanvasGroup labelGroup;
            private float collapsedWidth;
            private float expandedWidth;
            private float currentWidth;
            private float targetWidth;

            public void Initialize(
                RectTransform button,
                RectTransform label,
                CanvasGroup extension,
                CanvasGroup text,
                float collapsed,
                float expanded)
            {
                buttonRect = button;
                labelRect = label;
                extensionGroup = extension;
                labelGroup = text;
                collapsedWidth = collapsed;
                expandedWidth = expanded;
                currentWidth = collapsedWidth;
                targetWidth = collapsedWidth;
                ApplyState(0f);
            }

            public void OnPointerEnter(PointerEventData eventData)
            {
                targetWidth = expandedWidth;
            }

            public void OnPointerExit(PointerEventData eventData)
            {
                targetWidth = collapsedWidth;
            }

            private void Update()
            {
                if (buttonRect == null || labelRect == null)
                {
                    return;
                }

                currentWidth = Mathf.Lerp(currentWidth, targetWidth, Time.unscaledDeltaTime * AnimationSpeed);
                if (Mathf.Abs(currentWidth - targetWidth) < 0.5f)
                {
                    currentWidth = targetWidth;
                }

                var progress = Mathf.InverseLerp(collapsedWidth, expandedWidth, currentWidth);
                ApplyState(progress);
            }

            private void ApplyState(float progress)
            {
                buttonRect.sizeDelta = new Vector2(Mathf.Max(collapsedWidth, currentWidth), buttonRect.sizeDelta.y);
                labelRect.sizeDelta = new Vector2(Mathf.Max(0f, currentWidth - collapsedWidth + 4f), labelRect.sizeDelta.y);

                if (extensionGroup != null)
                {
                    extensionGroup.alpha = progress;
                }

                if (labelGroup != null)
                {
                    labelGroup.alpha = Mathf.Clamp01((progress - 0.28f) / 0.72f);
                }
            }
        }

        private static void EnsureEventSystem()
        {
            if (FindObjectOfType<EventSystem>() != null)
            {
                return;
            }

            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        }
    }
}
