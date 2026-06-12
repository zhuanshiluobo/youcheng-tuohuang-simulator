using System;
using System.Collections.Generic;
using YC.Application.Sessions;
using YC.Domain.Rules;
using YC.Infrastructure.Multiplayer;
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
            roomService.RoomUpdated += QueueRoomUpdate;
            roomService.GameStarted += QueueGameStart;
            roomService.RoomDisbanded += QueueRoomDisbanded;
            roomService.ErrorOccurred += QueueNetworkError;
            BuildMenu();
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
            CreateMenuButtons(coverFrame);
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
            CreateMenuButton(parent, "单机开始", 0, StartGame);
            CreateMenuButton(parent, "创建房间", 1, CreateRoom);
            CreateMenuButton(parent, "加入房间", 2, JoinRoom);
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
            text.font = Font.CreateDynamicFontFromOSFont(new[] { "SimHei", "Microsoft YaHei", "Arial" }, text.fontSize);

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
            inputRect.sizeDelta = new Vector2(420f, 46f);
            inputRect.anchoredPosition = new Vector2(0f, 10f);

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
            text.font = Font.CreateDynamicFontFromOSFont(new[] { "SimHei", "Microsoft YaHei", "Arial" }, text.fontSize);

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

            CreatePanelText(rect, "房间号 " + room.RoomId, 28, new Vector2(0f, 170f), FontStyle.Bold);

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
            loadingGame = true;
            var mode = room.LocalPlayerId == room.HostPlayerId ? LaunchMode.Host : LaunchMode.Client;
            GameLaunchContext.Ensure().Configure(mode, room.LocalPlayerId, room.RoomId, room.Seats);
            SceneManager.LoadScene(mapSceneName);
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
            text.font = Font.CreateDynamicFontFromOSFont(new[] { "SimHei", "Microsoft YaHei", "Arial" }, size);
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
            text.font = Font.CreateDynamicFontFromOSFont(new[] { "SimHei", "Microsoft YaHei", "Arial" }, text.fontSize);
        }

        private void SetRoomStatus(string message)
        {
            if (roomStatusText != null)
            {
                roomStatusText.text = message;
            }
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
