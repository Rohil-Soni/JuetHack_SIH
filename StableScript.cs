using UnityEngine;
using Vuforia;
using System;
using System.Collections.Generic;
using System.Text.Json;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Complete AR Stability Manager with Modern Vuforia Integration
/// Uses ObserverBehaviour instead of deprecated TrackableBehaviour
/// </summary>
public class StableScript : MonoBehaviour
{
    #region Configuration Properties
    [Header("=== AR CONFIGURATION ===")]
    [SerializeField] private GameObject targetGameObject;
    [SerializeField] private float smoothSpeed = 5f;

    [Header("=== VUFORIA CONFIGURATION ===")]
    [SerializeField] private ObserverBehaviour mObserverBehaviour;
    // Alternative: Use ImageTargetBehaviour specifically for image targets
    [SerializeField] private ImageTargetBehaviour imageTargetBehaviour;

    [Header("=== TRACKING ENHANCEMENT ===")]
    [SerializeField] private ARPlaneManager arPlaneManager;
    [SerializeField] private ARPointCloudManager arPointCloudManager;

    [Header("=== FALLBACK SETTINGS ===")]
    [SerializeField] private float gyroSensitivity = 1f;
    [SerializeField] private float smoothTranslationTime = 0.3f;

    [Header("=== PERFORMANCE SETTINGS ===")]
    [SerializeField] private int targetFPS = 60;
    [SerializeField] private bool avoidGarbageCollection = true;

    [Header("=== REDIS CONFIGURATION ===")]
    [SerializeField] private RedisHttpClient redisClient;
    [SerializeField] private bool enableCaching = true;
    #endregion

    #region Private Variables
    // Vuforia Tracking
    private bool isTracking = false;
    private TargetStatus previousStatus;

    // Anchor Management
    private List<AnchorData> anchorPoints = new List<AnchorData>();

    // Fallback Tracking
    private Vector3 lastGoodPosition;
    private Quaternion lastGoodRotation;
    private Vector3 velocity;

    // Performance Monitoring
    private float deltaTime;
    private string sessionId;

    // Static Instance for Global Access
    public static StableScript Instance { get; private set; }
    #endregion

    #region Unity Lifecycle Methods
    void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    void Start()
    {
        InitializeComponents();
        InitializeAR();
        InitializeVuforia();
        ConfigurePerformanceSettings();
        
        // Initialize Redis client
        if (redisClient == null)
            redisClient = new RedisHttpClient();
            
        if (enableCaching)
        {
            StartCoroutine(LoadCachedData());
        }
    }

    void Update()
    {
        MonitorPerformance();
        HandleFallbackTracking();

        if (avoidGarbageCollection && Time.frameCount % 600 == 0)
        {
            OptimizeMemoryUsage();
        }
    }

    void OnDestroy()
    {
        // Unregister from Vuforia events
        if (mObserverBehaviour != null)
        {
            mObserverBehaviour.OnTargetStatusChanged -= OnObserverStatusChanged;
        }
        
        if (imageTargetBehaviour != null)
        {
            imageTargetBehaviour.OnTargetStatusChanged -= OnObserverStatusChanged;
        }
    }
    #endregion

    #region Initialization Methods
    private void InitializeComponents()
    {
        sessionId = System.Guid.NewGuid().ToString();
        Debug.Log("[AR Stability] Components initialized successfully");
    }

    private void InitializeAR()
    {
        ConfigureARPlanes();
        ConfigurePointClouds();
        EnableGyroscope();
    }

    private void InitializeVuforia()
    {
        // Get ObserverBehaviour if not assigned
        if (mObserverBehaviour == null)
        {
            mObserverBehaviour = GetComponent<ObserverBehaviour>();
        }
        
        // Get ImageTargetBehaviour if not assigned (for image targets specifically)
        if (imageTargetBehaviour == null)
        {
            imageTargetBehaviour = GetComponent<ImageTargetBehaviour>();
        }

        // Register for status change events
        if (mObserverBehaviour != null)
        {
            mObserverBehaviour.OnTargetStatusChanged += OnObserverStatusChanged;
            Debug.Log("[Vuforia] Observer event handler registered");
        }
        else if (imageTargetBehaviour != null)
        {
            imageTargetBehaviour.OnTargetStatusChanged += OnObserverStatusChanged;
            Debug.Log("[Vuforia] Image target event handler registered");
        }
        else
        {
            Debug.LogError("[Vuforia] No ObserverBehaviour or ImageTargetBehaviour found!");
        }
    }

    private void ConfigurePerformanceSettings()
    {
        Application.targetFrameRate = targetFPS;
        QualitySettings.vSyncCount = 0;
        Debug.Log($"[Performance] Target FPS set to {targetFPS}");
    }
    #endregion

    #region Modern Vuforia Event Handlers
    /// <summary>
    /// Modern Vuforia event handler - replaces deprecated ITrackableEventHandler
    /// </summary>
    private void OnObserverStatusChanged(ObserverBehaviour behaviour, TargetStatus targetStatus)
    {
        HandleTrackingState(targetStatus);
        Debug.Log($"[Vuforia] Target: {behaviour.TargetName}, Status: {targetStatus.Status}, StatusInfo: {targetStatus.StatusInfo}");
    }

    private void HandleTrackingState(TargetStatus targetStatus)
    {
        if (targetStatus.Status == Status.TRACKED || 
            targetStatus.Status == Status.EXTENDED_TRACKED)
        {
            OnTrackingFound();
        }
        else if (targetStatus.Status == Status.NO_POSE || 
                 targetStatus.Status == Status.LIMITED)
        {
            OnTrackingLost();
        }

        previousStatus = targetStatus;
    }

    private void OnTrackingFound()
    {
        isTracking = true;
        if (targetGameObject != null)
        {
            targetGameObject.SetActive(true);
        }
        
        SaveAnchorData(transform.position, transform.rotation, 1.0f);
        SaveLastGoodPosition(transform.position, transform.rotation);
        Debug.Log("[Vuforia] Target found and tracked");
    }

    private void OnTrackingLost()
    {
        isTracking = false;
        Debug.Log("[Vuforia] Target lost - activating fallback mechanisms");
    }
    #endregion

    // ... [Rest of your existing methods remain the same - AnchorData, Redis implementation, etc.] ...
    // I'll include the essential ones for completeness:

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

        if (anchorPoints.Count > 10)
        {
            anchorPoints.RemoveAt(0);
        }

        if (enableCaching)
        {
            StartCoroutine(SetAnchorDataInCache($"anchor_{anchor.timestamp}", anchor));
        }
        
        Debug.Log($"[Anchor] Saved anchor with confidence: {confidence:F2}");
    }
    #endregion

    #region Tracking Enhancement System
    private void ConfigureARPlanes()
    {
        if (arPlaneManager != null)
        {
            arPlaneManager.enabled = true;
            arPlaneManager.requestedDetectionMode = PlaneDetectionMode.Everything;
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
            Quaternion gyroRotation = Input.gyro.attitude;
            transform.rotation = Quaternion.Slerp(transform.rotation, lastGoodRotation * gyroRotation, Time.deltaTime * gyroSensitivity);
        }
        transform.position = Vector3.SmoothDamp(transform.position, lastGoodPosition, ref velocity, smoothTranslationTime);
    }

    public void SaveLastGoodPosition(Vector3 position, Quaternion rotation)
    {
        lastGoodPosition = position;
        lastGoodRotation = rotation;
        
        if (enableCaching)
        {
            StartCoroutine(SetLastGoodPositionInCache(position, rotation));
        }
    }
    #endregion

    #region Performance Optimization System
    private void MonitorPerformance()
    {
        deltaTime += (Time.unscaledDeltaTime - deltaTime) * 0.1f;
        if (Time.frameCount % 300 == 0)
        {
            float currentFPS = 1.0f / deltaTime;
            if (enableCaching)
            {
                CachePerformanceMetrics(sessionId, currentFPS, deltaTime);
            }
        }
    }

    private void OptimizeMemoryUsage()
    {
        Resources.UnloadUnusedAssets();
    }

    private void CachePerformanceMetrics(string sessionId, float fps, float frameTime)
    {
        if (!enableCaching) return;

        var metrics = new PerformanceMetrics
        {
            sessionId = sessionId,
            fps = fps,
            frameTime = frameTime,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            anchorCount = anchorPoints.Count,
            isTracking = isTracking
        };

        string jsonData = JsonUtility.ToJson(metrics);
        StartCoroutine(redisClient.SetInRedis($"perf_{sessionId}", jsonData, 1800));
    }
    #endregion

    // [Include your Redis implementation methods here...]
    
    #region Debug and Utilities
    #if UNITY_EDITOR
    void OnGUI()
    {
        GUI.Box(new Rect(10, 10, 300, 190), "AR Stability Debug (Modern Vuforia)");
        GUI.Label(new Rect(20, 30), $"Tracking: {(isTracking ? "ACTIVE" : "LOST")}");
        GUI.Label(new Rect(20, 50), $"Anchors: {anchorPoints.Count}");
        GUI.Label(new Rect(20, 70), $"FPS: {(1.0f / deltaTime):F1}");
        GUI.Label(new Rect(20, 90), $"Redis: {(enableCaching ? "Enabled" : "Disabled")}");
        GUI.Label(new Rect(20, 110), $"Gyro: {(Input.gyro.enabled ? "Enabled" : "Disabled")}");
        GUI.Label(new Rect(20, 130), $"Session: {sessionId.Substring(0, 8)}...");
        
        string targetName = mObserverBehaviour != null ? mObserverBehaviour.TargetName : "None";
        GUI.Label(new Rect(20, 150), $"Target: {targetName}");

        if (GUI.Button(new Rect(20, 170, 100, 20), "Clear Cache"))
        {
            ClearAllCache();
        }
    }
    #endif
    #endregion
}