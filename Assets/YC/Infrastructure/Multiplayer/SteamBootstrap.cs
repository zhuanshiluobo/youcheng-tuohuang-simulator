using System;
using Steamworks;
using UnityEngine;
namespace YC.Infrastructure.Multiplayer
{
    [DefaultExecutionOrder(-25000)]
    public sealed class SteamBootstrap : MonoBehaviour
    {
        public static SteamBootstrap Instance { get; private set; }
        public static bool IsInitialized { get; private set; }
        public event Action<string> InitializationFailed;
        public static SteamBootstrap Ensure()
        {
            if (Instance == null)
            {
                throw new InvalidOperationException(
                    "缺少预接线的 SteamBootstrap。请重建 NetworkRuntimeRoot Prefab 并确认 StartScene 接线完整。");
            }
            return Instance;
        }
        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                gameObject.SetActive(false);
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        public bool Initialize()
        {
            if (IsInitialized) return true;
            try
            {
                IsInitialized = SteamAPI.Init();
                if (!IsInitialized)
                {
                    Fail("Steam 初始化失败。请先启动 Steam 客户端并登录，再重新启动游戏。");
                    return false;
                }
                if (SteamUtils.GetAppID().m_AppId != SteamLobbyPolicy.AppId)
                {
                    SteamAPI.Shutdown();
                    IsInitialized = false;
                    Fail("当前 Steam AppID 不是 480，请检查 steam_appid.txt。");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                IsInitialized = false;
                Fail("Steam 初始化失败：" + ex.Message);
                return false;
            }
        }
        private void Update()
        {
            if (IsInitialized) SteamAPI.RunCallbacks();
        }
        private void OnApplicationQuit() => Shutdown();
        private void OnDestroy()
        {
            if (Instance == this)
            {
                Shutdown();
                Instance = null;
            }
        }
        public void Shutdown()
        {
            if (!IsInitialized) return;
            SteamAPI.Shutdown();
            IsInitialized = false;
        }
        private void Fail(string message)
        {
            Debug.LogWarning(message);
            InitializationFailed?.Invoke(message);
        }
    }
}