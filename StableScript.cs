using UnityEngine;
using Vuforia;
using StackExchange.Redis;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;

/// <summary>
/// Complete AR Stability Manager with Vuforia Integration and Redis Caching
/// Handles anchor management, tracking enhancement, fallback mechanisms, and performance optimization
/// </summary>
public class StableScript : MonoBehaviour, ITrackableEventHandler
{
    #region Configuration Properties
    [Header("=== AR CONFIGURATION ===")]
    [SerializeField] private Transform anchorPoint;
    [SerializeField] private GameObject targetGameObject;
    [SerializeField] private float smoothSpeed = 5f;
    [SerializeField] private float driftThreshold = 0.1f;

    [Header("=== REDIS CONFIGURATION ===")]
    [SerializeField] private string connectionString = "localhost:6379";
    [SerializeField] private string instanceName = "ARApp";

    [Header("=== TRACKING ENHANCEMENT ===")]
    [SerializeField] private ARPlaneManager arPlaneManager;
    [SerializeField] private ARPointCloudManager arPointCloudManager;
    [SerializeField] private ARSessionOrigin arSessionOrigin;

    [Header("=== FALLBACK SETTINGS ===")]
    [SerializeField] private float gyroSensitivity = 1f;
    [SerializeField] private float smoothTranslationTime = 0.3f;

    [Header("=== PERFORMANCE SETTINGS ===")]
    [SerializeField] private int targetFPS = 60;
    [SerializeField] private bool avoidGarbageCollection = true;
    #endregion

    #region Private Variables
    // Vuforia Tracking Variables
    private TrackableBehaviour mTrackableBehaviour;
    private Vector3 lastKnownPosition;
    private Quaternion lastKnownRotation;
    private bool isTracking = false;

    // Redis Connection
    private IDatabase database;
    private ConnectionMultiplexer redis;

    // Anchor Management
    private List<AnchorData> anchorPoints = new List<AnchorData>();

    // Fallback Tracking
    private Vector3 lastGoodPosition;
    private Quaternion lastGoodRotation;
    private Vector3 velocity;

    // Performance Monitoring
    private float deltaTime;
    private string sessionId;
    private Camera arCamera;

    // Static Instance for Global Access
    public static StableScript Instance { get; private set; }
    public static bool IsTracking => Instance != null ? Instance.isTracking : false;
    #endregion

    #region Unity Lifecycle Methods
    void Awake()
    {
        // Singleton Pattern
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
            return;
        }
    }

    private void ConfigureARPlanes()
    {
        if (arPlaneManager != null)
        {
            arPlaneManager.enabled = true;
            arPlaneManager.requestedDetectionMode =
                PlaneDetectionMode.Everything;

            Debug.Log("[AR Planes] Configured for full detection mode");
        }
    }

    void Start()
    {
        ConfigureARPlanes();
        InitializeComponents();
        InitializeRedis();
        InitializeAR();
        ConfigurePerformanceSettings();
        LoadCachedData();
    }

    void Update()
    {
        MonitorPerformance();
        HandleFallbackTracking();

        if (avoidGarbageCollection)
        {
            OptimizeMemoryUsage();
        }
    }

    void OnDestroy()
    {
        redis?.Dispose();
    }
    #endregion

    #region Initialization Methods
    private void InitializeComponents()
    {
        arCamera = Camera.main;
        sessionId = System.Guid.NewGuid().ToString();

        Debug.Log("[AR Stability] Components initialized successfully");
    }

    private void InitializeRedis()
    {
        try
        {
            redis = ConnectionMultiplexer.Connect(connectionString);
            database = redis.GetDatabase();
            Debug.Log("[Redis] Connected successfully to " + connectionString);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Redis] Connection failed: {ex.Message}");
        }
    }

    private void InitializeAR()
    {
        mTrackableBehaviour = GetComponent<TrackableBehaviour>();
        if (mTrackableBehaviour)
        {
            mTrackableBehaviour.RegisterTrackableEventHandler(this);
            Debug.Log("[AR] Trackable event handler registered");
        }

        ConfigureARPlanes();
        ConfigurePointClouds();
        EnableGyroscope();
    }

    private void ConfigurePerformanceSettings()
    {
        Application.targetFrameRate = targetFPS;
        QualitySettings.vSyncCount = 0;

        Debug.Log($"[Performance] Target FPS set to {targetFPS}");
    }

    private void LoadCachedData()
    {
        LoadAnchorsFromCache();
        LoadLastGoodPositionFromCache();

        Debug.Log("[Cache] Loaded cached AR data");
    }
    #endregion

    #region AR Tracking Event Handlers
    public void OnTrackableStateChanged(
        TrackableBehaviour.Status previousStatus,
        TrackableBehaviour.Status newStatus)
    {
        HandleTrackingState(newStatus);
        Debug.Log($"[AR Tracking] State changed from {previousStatus} to {newStatus}");
    }

    private void HandleTrackingState(TrackableBehaviour.Status status)
    {
        if (status == TrackableBehaviour.Status.DETECTED ||
            status == TrackableBehaviour.Status.TRACKED ||
            status == TrackableBehaviour.Status.EXTENDED_TRACKED)
        {
            OnTrackingFound();
        }
        else
        {
            OnTrackingLost();
        }
    }

    private void OnTrackingFound()
    {
        isTracking = true;

        if (targetGameObject != null)
        {
            targetGameObject.SetActive(true);
        }

        // Save current position as anchor
        SaveAnchorData(transform.position, transform.rotation, 1.0f);
        SaveLastGoodPosition(transform.position, transform.rotation);

        Debug.Log("[AR Tracking] Target found and tracked");
    }

    private void OnTrackingLost()
    {
        isTracking = false;

        Debug.Log("[AR Tracking] Target lost - activating fallback mechanisms");
    }
    #endregion

    #region Anchor Management System
    [System.Serializable]
    public class AnchorData
    {
        public Vector3 position;
        public Quaternion rotation;
        public float confidence;
        public long timestamp;

        public AnchorData() { }

        public AnchorData(Vector3 pos, Quaternion rot, float conf)
        {
            position = pos;
            rotation = rot;
            confidence = conf;
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
    }

    public void SaveAnchorData(Vector3 position, Quaternion rotation, float confidence)
    {
        AnchorData anchor = new AnchorData(position, rotation, confidence);
        anchorPoints.Add(anchor);

        // Keep only last 10 anchors for performance
        if (anchorPoints.Count > 10)
        {
            anchorPoints.RemoveAt(0);
        }

        // Cache in Redis
        SetAnchorDataInCache("anchor_" + anchor.timestamp, anchor);

        Debug.Log($"[Anchor] Saved anchor with confidence: {confidence:F2}");
    }

    public AnchorData GetBestAnchor()
    {
        if (anchorPoints.Count == 0) return null;

        AnchorData bestAnchor = anchorPoints[0];
        foreach (var anchor in anchorPoints)
        {
            if (anchor.confidence > bestAnchor.confidence)
                bestAnchor = anchor;
        }

        return bestAnchor;
    }

    private void LoadAnchorsFromCache()
    {
        try
        {
            // Load recent anchors from Redis
            var keys = database.Execute("KEYS", "anchor:anchor_*");
            if (keys != null)
            {
                foreach (var key in (RedisValue[])keys)
                {
                    var anchorData = GetAnchorDataFromCache(key.ToString().Replace("anchor:", ""));
                    if (anchorData != null)
                    {
                        anchorPoints.Add(anchorData);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Cache] Failed to load anchors: {ex.Message}");
        }
    }
    #endregion

    #region Tracking Enhancement System
    private void ConfigureARPlanes()
    {
        if (arPlaneManager != null)
        {
            arPlaneManager.enabled = true;
            arPlaneManager.requestedDetectionMode =
                UnityEngine.XR.ARSubsystems.PlaneDetectionMode.Everything;

            Debug.Log("[AR Planes] Configured for full detection mode");
        }
    }

    private void ConfigurePointClouds()
    {
        if (arPointCloudManager != null)
        {
            arPointCloudManager.enabled = true;
            Debug.Log("[AR Point Clouds] Enabled for enhanced tracking");
        }
    }

    public void FineTunePlaneSettings()
    {
        if (arPlaneManager == null) return;

        // Cache plane data for stability
        var planes = arPlaneManager.trackables;
        List<PlaneData> planeDataList = new List<PlaneData>();

        foreach (var plane in planes)
        {
            PlaneData data = new PlaneData
            {
                position = plane.transform.position,
                rotation = plane.transform.rotation,
                size = plane.size
            };
            planeDataList.Add(data);
        }

        SetPlaneDataInCache("detected_planes", planeDataList);
        Debug.Log($"[AR Planes] Cached {planeDataList.Count} detected planes");
    }
    #endregion

    #region Fallback Mechanism System
    private void EnableGyroscope()
    {
        if (SystemInfo.supportsGyroscope)
        {
            Input.gyro.enabled = true;
            Debug.Log("[Gyroscope] Enabled for fallback tracking");
        }
        else
        {
            Debug.LogWarning("[Gyroscope] Not supported on this device");
        }
    }

    private void HandleFallbackTracking()
    {
        if (!isTracking && lastGoodPosition != Vector3.zero)
        {
            UseInertialTracking();
        }
    }

    private void UseInertialTracking()
    {
        if (SystemInfo.supportsGyroscope)
        {
            // Use gyroscope data for rotation
            Quaternion gyroRotation = Input.gyro.attitude;
            transform.rotation = Quaternion.Slerp(transform.rotation,
                gyroRotation, Time.deltaTime * gyroSensitivity);
        }

        // Smooth translation to last known good position
        transform.position = Vector3.SmoothDamp(transform.position,
            lastGoodPosition, ref velocity, smoothTranslationTime);
    }

    public void SaveLastGoodPosition(Vector3 position, Quaternion rotation)
    {
        lastGoodPosition = position;
        lastGoodRotation = rotation;

        // Cache the good position
        SetLastGoodPositionInCache(position, rotation);
    }

    private void LoadLastGoodPositionFromCache()
    {
        try
        {
            string posData = database.StringGet("lastGoodPosition");
            string rotData = database.StringGet("lastGoodRotation");

            if (!string.IsNullOrEmpty(posData) && !string.IsNullOrEmpty(rotData))
            {
                lastGoodPosition = JsonSerializer.Deserialize<Vector3>(posData);
                lastGoodRotation = JsonSerializer.Deserialize<Quaternion>(rotData);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Cache] Failed to load last good position: {ex.Message}");
        }
    }
    #endregion

    #region Performance Optimization System
    private void MonitorPerformance()
    {
        deltaTime += (Time.unscaledDeltaTime - deltaTime) * 0.1f;
        float currentFPS = 1.0f / deltaTime;

        // Cache performance metrics every 5 seconds
        if (Time.time % 5f < Time.deltaTime)
        {
            CachePerformanceMetrics(sessionId, currentFPS, deltaTime);
        }
    }

    private void OptimizeMemoryUsage()
    {
        // Limit garbage collection by periodically unloading unused assets
        if (Time.time % 10f < Time.deltaTime)
        {
            Resources.UnloadUnusedAssets();
        }
    }

    private void CachePerformanceMetrics(string sessionId, float fps, float frameTime)
    {
        try
        {
            var metrics = new { fps = fps, frameTime = frameTime, timestamp = DateTime.UtcNow };
            string jsonData = JsonSerializer.Serialize(metrics);
            database.StringSet($"perf:{sessionId}", jsonData, TimeSpan.FromHours(1));
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Performance] Failed to cache metrics: {ex.Message}");
        }
    }
    #endregion

    #region Redis Caching System
    // Anchor Data Caching
    private void SetAnchorDataInCache(string key, AnchorData anchorData)
    {
        try
        {
            string jsonData = JsonSerializer.Serialize(anchorData);
            database.StringSet($"anchor:{key}", jsonData, TimeSpan.FromHours(24));
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Redis] Failed to cache anchor data: {ex.Message}");
        }
    }

    private AnchorData GetAnchorDataFromCache(string key)
    {
        try
        {
            string jsonData = database.StringGet($"anchor:{key}");
            if (!string.IsNullOrEmpty(jsonData))
            {
                return JsonSerializer.Deserialize<AnchorData>(jsonData);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Redis] Failed to retrieve anchor data: {ex.Message}");
        }
        return null;
    }

    // Render Data Caching
    public void SetRenderData(string modelId, RenderData renderData)
    {
        try
        {
            string jsonData = JsonSerializer.Serialize(renderData);
            database.StringSet($"render:{modelId}", jsonData, TimeSpan.FromMinutes(30));
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Redis] Failed to cache render data: {ex.Message}");
        }
    }

    public RenderData GetRenderData(string modelId)
    {
        try
        {
            string jsonData = database.StringGet($"render:{modelId}");
            if (!string.IsNullOrEmpty(jsonData))
            {
                return JsonSerializer.Deserialize<RenderData>(jsonData);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Redis] Failed to retrieve render data: {ex.Message}");
        }
        return null;
    }

    // Plane Detection Data Caching
    private void SetPlaneDataInCache(string key, List<PlaneData> planeData)
    {
        try
        {
            string jsonData = JsonSerializer.Serialize(planeData);
            database.StringSet($"plane:{key}", jsonData, TimeSpan.FromMinutes(10));
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Redis] Failed to cache plane data: {ex.Message}");
        }
    }

    // Position Data Caching
    private void SetLastGoodPositionInCache(Vector3 position, Quaternion rotation)
    {
        try
        {
            string posJson = JsonSerializer.Serialize(position);
            string rotJson = JsonSerializer.Serialize(rotation);

            database.StringSet("lastGoodPosition", posJson, TimeSpan.FromHours(1));
            database.StringSet("lastGoodRotation", rotJson, TimeSpan.FromHours(1));
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Redis] Failed to cache position data: {ex.Message}");
        }
    }
    #endregion

    #region Public API Methods
    /// <summary>
    /// Manually trigger anchor saving with custom confidence level
    /// </summary>
    public void ManualSaveAnchor(float confidence = 0.8f)
    {
        SaveAnchorData(transform.position, transform.rotation, confidence);
    }

    /// <summary>
    /// Force fallback mode for testing
    /// </summary>
    public void ActivateFallbackMode()
    {
        isTracking = false;
        Debug.Log("[AR] Fallback mode activated manually");
    }

    /// <summary>
    /// Get current tracking status
    /// </summary>
    public bool GetTrackingStatus()
    {
        return isTracking;
    }

    /// <summary>
    /// Clear all cached data
    /// </summary>
    public void ClearAllCache()
    {
        try
        {
            anchorPoints.Clear();
            database.Execute("FLUSHALL");
            Debug.Log("[Cache] All cached data cleared");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Cache] Failed to clear cache: {ex.Message}");
        }
    }
    #endregion

    #region Data Structures
    [System.Serializable]
    public class RenderData
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 scale;
        public int materialId;
        public bool isVisible;
    }

    [System.Serializable]
    public class PlaneData
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector2 size;
    }
    #endregion

    #region Debug and Utilities
    void OnGUI()
    {
        if (!Application.isEditor) return;

        GUI.Box(new Rect(10, 10, 300, 150), "AR Stability Debug");
        GUI.Label(new Rect(20, 30), $"Tracking: {(isTracking ? "ACTIVE" : "LOST")}");
        GUI.Label(new Rect(20, 50), $"Anchors: {anchorPoints.Count}");
        GUI.Label(new Rect(20, 70), $"FPS: {(1.0f / deltaTime):F1}");
        GUI.Label(new Rect(20, 90), $"Redis: {(database != null ? "Connected" : "Disconnected")}");
        GUI.Label(new Rect(20, 110), $"Gyro: {(Input.gyro.enabled ? "Enabled" : "Disabled")}");

        if (GUI.Button(new Rect(20, 130, 100, 20), "Clear Cache"))
        {
            ClearAllCache();
        }
    }
    #endregion
    
    #region Debug and Utilities
        #if UNITY_EDITOR
    void OnGUI()
    {
        GUI.Box(new Rect(10, 10, 300, 150), "AR Stability Debug");
        GUI.Label(new Rect(20, 30), $"Tracking: {(isTracking ? "ACTIVE" : "LOST")}");
        GUI.Label(new Rect(20, 50), $"Anchors: {anchorPoints.Count}");
        GUI.Label(new Rect(20, 70), $"FPS: {(1.0f / deltaTime):F1}");
        GUI.Label(new Rect(20, 90), $"Redis: {(database != null ? "Connected" : "Disconnected")}");
        GUI.Label(new Rect(20, 110), $"Gyro: {(Input.gyro.enabled ? "Enabled" : "Disabled")}");
        
        if (GUI.Button(new Rect(20, 130, 100, 20), "Clear Cache"))
        {
            ClearAllCache();
        }
    }
    #endif
    #endregion
}