using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using Vuforia;
using GLTFast;
using UnityEngine.Networking;
using System.IO;

/// <summary>
/// Vuforia + AWS Redis + S3 Model Loader with LOCAL Mobile Anchor Caching
/// Stores anchor data locally and auto-deletes on app exit
/// </summary>
public class VuforiaAWSRedisLoader : MonoBehaviour
{
    [Header("=== AWS REDIS CONFIGURATION ===")]
    public string apiBaseUrl = "http://43.205.215.100:5000";
    public string[] availableModelKeys = {"model:temple1", "model:temple2", "model:building1"};
    public bool autoLoadModels = true;
    
    [Header("=== VUFORIA GROUND PLANE COMPONENTS ===")]
    public PlaneFinderBehaviour planeFinder;
    public GameObject groundPlaneStage;
    public ContentPositioningBehaviour contentPositioning;
    
    [Header("=== AR STABILITY SETTINGS ===")]
    public float stabilityThreshold = 0.1f;
    public float smoothingSpeed = 5f;
    public int anchorUpdateFrames = 10;
    public bool enableDriftCorrection = true;
    
    [Header("=== LOCAL ANCHOR CACHING ===")]
    public bool enableLocalAnchorCache = true;
    public float autoSaveInterval = 10f; // Save every 10 seconds
    public bool deleteOnAppExit = true; // Auto-delete when app closes
    
    [Header("=== MODEL CONFIGURATION ===")]
    public float modelScale = 0.1f;
    public GameObject[] fallbackPrefabs;
    
    [Header("=== PHYSICS CONFIGURATION ===")]
    public bool addPhysicsComponents = true;
    public bool makeCollidersConvex = true;
    public bool enableGravity = true;
    public float defaultMass = 1f;
    public PhysicMaterial defaultPhysicMaterial;
    
    // Private Variables
    private bool groundPlaneInitialized = false;
    private Dictionary<string, StableModelData> loadedModels = new Dictionary<string, StableModelData>();
    private Queue<string> modelLoadQueue = new Queue<string>();
    private int currentModelIndex = 0;
    private int frameCounter = 0;
    
    // Anchor System
    private Vector3 lastGroundPlanePosition;
    private Quaternion lastGroundPlaneRotation;
    private bool hasValidAnchor = false;
    
    // Local Caching System
    private string anchorCacheFilePath;
    private string sessionId;
    private bool hasUnsavedAnchorData = false;
    private Coroutine autoSaveCoroutine;
    
    // Performance
    private float deltaTime;
    
    void Start()
    {
        InitializeSystem();
        StartCoroutine(ProcessModelQueue());
        StartCoroutine(StabilityUpdateLoop());
        
        if (enableLocalAnchorCache)
        {
            InitializeLocalAnchorCache();
        }
    }
    
    void Update()
    {
        MonitorPerformance();
        frameCounter++;
    }
    
    // ============== LOCAL ANCHOR CACHING SYSTEM ==============
    void InitializeLocalAnchorCache()
    {
        sessionId = System.Guid.NewGuid().ToString().Substring(0, 8);
        
        // Create cache file path in persistent data path
        string cacheFileName = $"ar_anchors_{sessionId}.json";
        anchorCacheFilePath = Path.Combine(Application.persistentDataPath, cacheFileName);
        
        Debug.Log($"[Local Cache] Initialized - File: {anchorCacheFilePath}");
        
        // Load existing anchor data if available
        LoadLocalAnchorCache();
        
        // Start auto-save coroutine
        if (autoSaveCoroutine == null)
        {
            autoSaveCoroutine = StartCoroutine(AutoSaveAnchors());
        }
        
        // Register cleanup events
        RegisterCleanupEvents();
    }
    
    void RegisterCleanupEvents()
    {
        // Register for app lifecycle events
        Application.focusChanged += OnApplicationFocus;
        Application.quitting += OnApplicationQuitting;
        
        Debug.Log("[Local Cache] Cleanup events registered");
    }
    
    void LoadLocalAnchorCache()
    {
        if (!enableLocalAnchorCache || !File.Exists(anchorCacheFilePath)) return;
        
        try
        {
            string jsonData = File.ReadAllText(anchorCacheFilePath);
            LocalAnchorData anchorData = JsonUtility.FromJson<LocalAnchorData>(jsonData);
            
            if (anchorData != null)
            {
                ApplyLocalAnchorData(anchorData);
                Debug.Log($"✅ [Local Cache] Loaded anchor data from file");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Local Cache] Failed to load: {e.Message}");
            // Delete corrupted file
            if (File.Exists(anchorCacheFilePath))
            {
                File.Delete(anchorCacheFilePath);
            }
        }
    }
    
    void ApplyLocalAnchorData(LocalAnchorData anchorData)
    {
        // Apply cached ground plane
        if (anchorData.groundPlanePosition != Vector3.zero)
        {
            lastGroundPlanePosition = anchorData.groundPlanePosition;
            lastGroundPlaneRotation = anchorData.groundPlaneRotation;
            hasValidAnchor = true;
            
            Debug.Log($"[Local Cache] Restored ground plane: {anchorData.groundPlanePosition}");
        }
        
        Debug.Log($"[Local Cache] Applied {anchorData.modelPositions.Count} cached model positions");
    }
    
    void SaveLocalAnchorCache()
    {
        if (!enableLocalAnchorCache || !hasUnsavedAnchorData) return;
        
        try
        {
            LocalAnchorData anchorData = new LocalAnchorData
            {
                sessionId = sessionId,
                timestamp = System.DateTime.Now.ToString(),
                groundPlanePosition = lastGroundPlanePosition,
                groundPlaneRotation = lastGroundPlaneRotation,
                modelPositions = new List<LocalModelPosition>()
            };
            
            // Add all current model positions
            foreach (var kvp in loadedModels)
            {
                if (kvp.Value.modelObject != null)
                {
                    anchorData.modelPositions.Add(new LocalModelPosition
                    {
                        modelKey = kvp.Key,
                        position = kvp.Value.anchorPosition,
                        rotation = kvp.Value.anchorRotation,
                        isStable = kvp.Value.isStable
                    });
                }
            }
            
            string jsonData = JsonUtility.ToJson(anchorData, true);
            File.WriteAllText(anchorCacheFilePath, jsonData);
            
            hasUnsavedAnchorData = false;
            Debug.Log($"💾 [Local Cache] Saved {anchorData.modelPositions.Count} model anchors");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[Local Cache] Save failed: {e.Message}");
        }
    }
    
    IEnumerator AutoSaveAnchors()
    {
        while (true)
        {
            yield return new WaitForSeconds(autoSaveInterval);
            
            if (hasUnsavedAnchorData && groundPlaneInitialized)
            {
                SaveLocalAnchorCache();
            }
        }
    }
    
    // ============== CLEANUP SYSTEM ==============
    void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus && enableLocalAnchorCache)
        {
            Debug.Log("[Local Cache] App lost focus - saving anchors");
            SaveLocalAnchorCache();
        }
    }
    
    void OnApplicationQuitting()
    {
        CleanupOnExit();
    }
    
    void OnApplicationPause(bool pauseStatus)
    {
        if (pauseStatus && enableLocalAnchorCache)
        {
            Debug.Log("[Local Cache] App paused - saving anchors");
            SaveLocalAnchorCache();
        }
    }
    
    void CleanupOnExit()
    {
        Debug.Log("[Local Cache] App exiting - cleaning up anchor data");
        
        if (deleteOnAppExit && enableLocalAnchorCache)
        {
            DeleteLocalAnchorCache();
        }
        else if (enableLocalAnchorCache)
        {
            // Save one final time before exit
            SaveLocalAnchorCache();
        }
        
        // Stop auto-save coroutine
        if (autoSaveCoroutine != null)
        {
            StopCoroutine(autoSaveCoroutine);
            autoSaveCoroutine = null;
        }
        
        // Unregister events
        Application.focusChanged -= OnApplicationFocus;
        Application.quitting -= OnApplicationQuitting;
    }
    
    void DeleteLocalAnchorCache()
    {
        try
        {
            if (File.Exists(anchorCacheFilePath))
            {
                File.Delete(anchorCacheFilePath);
                Debug.Log("🗑️ [Local Cache] Deleted anchor cache file on exit");
            }
            
            // Also clean up any old cache files
            string cacheDirectory = Application.persistentDataPath;
            string[] oldCacheFiles = Directory.GetFiles(cacheDirectory, "ar_anchors_*.json");
            
            foreach (string oldFile in oldCacheFiles)
            {
                try
                {
                    File.Delete(oldFile);
                    Debug.Log($"🗑️ [Local Cache] Deleted old cache: {Path.GetFileName(oldFile)}");
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[Local Cache] Failed to delete {oldFile}: {e.Message}");
                }
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[Local Cache] Cleanup failed: {e.Message}");
        }
    }
    
    // ============== PUBLIC CACHE MANAGEMENT ==============
    public void ForceSaveAnchors()
    {
        if (enableLocalAnchorCache)
        {
            hasUnsavedAnchorData = true;
            SaveLocalAnchorCache();
            Debug.Log("[Local Cache] Force saved anchor data");
        }
    }
    
    public void ClearCachedAnchors()
    {
        if (enableLocalAnchorCache)
        {
            DeleteLocalAnchorCache();
            hasValidAnchor = false;
            lastGroundPlanePosition = Vector3.zero;
            Debug.Log("[Local Cache] Manually cleared cached anchors");
        }
    }
    
    public string GetCacheInfo()
    {
        if (!enableLocalAnchorCache) return "Cache disabled";
        
        if (File.Exists(anchorCacheFilePath))
        {
            FileInfo fileInfo = new FileInfo(anchorCacheFilePath);
            return $"Cache: {fileInfo.Length} bytes, Modified: {fileInfo.LastWriteTime:HH:mm:ss}";
        }
        
        return "No cache file";
    }
    
    // ============== STABILITY SYSTEM (SAME AS BEFORE) ==============
    [System.Serializable]
    public class StableModelData
    {
        public GameObject modelObject;
        public Vector3 anchorPosition;
        public Quaternion anchorRotation;
        public Vector3 targetPosition;
        public Vector3 velocity;
        public bool isStable;
        public float lastStableTime;
        
        public StableModelData(GameObject obj)
        {
            modelObject = obj;
            anchorPosition = obj.transform.position;
            anchorRotation = obj.transform.rotation;
            targetPosition = anchorPosition;
            velocity = Vector3.zero;
            isStable = false;
            lastStableTime = Time.time;
        }
        
        public void UpdateAnchor(Vector3 newPosition, Quaternion newRotation)
        {
            anchorPosition = newPosition;
            anchorRotation = newRotation;
            targetPosition = newPosition;
        }
        
        public void UpdateStability(float stabilityThreshold)
        {
            float movement = Vector3.Distance(modelObject.transform.position, targetPosition);
            isStable = movement < stabilityThreshold;
            if (isStable)
            {
                lastStableTime = Time.time;
            }
        }
    }
    
    void InitializeSystem()
    {
        Application.targetFrameRate = 60;
        Debug.Log($"[AWS Redis] Initializing system with local caching");
        
        InitializeVuforiaComponents();
        
        if (autoLoadModels)
        {
            foreach (string modelKey in availableModelKeys)
            {
                modelLoadQueue.Enqueue(modelKey);
            }
        }
    }
    
    void InitializeVuforiaComponents()
    {
        if (planeFinder == null)
            planeFinder = FindObjectOfType<PlaneFinderBehaviour>();

        if (groundPlaneStage == null)
            groundPlaneStage = GameObject.Find("Ground Plane Stage");

        if (contentPositioning == null)
            contentPositioning = FindObjectOfType<ContentPositioningBehaviour>();

        SetupVuforiaEvents();
    }
    
    void SetupVuforiaEvents()
    {
        if (planeFinder != null)
        {
            planeFinder.OnAutomaticHitTest.AddListener(OnGroundPlaneDetected);
            planeFinder.OnInteractiveHitTest.AddListener(OnInteractiveGroundPlaneHit);
            Debug.Log("[Vuforia] Ground plane event listeners registered");
        }
        else
        {
            Debug.LogError("[Vuforia] PlaneFinderBehaviour not found! Add it to your scene.");
        }
    }
    
    IEnumerator StabilityUpdateLoop()
    {
        while (true)
        {
            if (groundPlaneInitialized && enableDriftCorrection)
            {
                UpdateModelStability();
            }
            
            yield return new WaitForSeconds(0.1f);
        }
    }
    
    void UpdateModelStability()
    {
        if (!hasValidAnchor) return;
        
        Vector3 currentGroundPosition = groundPlaneStage.transform.position;
        Quaternion currentGroundRotation = groundPlaneStage.transform.rotation;
        
        float positionDrift = Vector3.Distance(currentGroundPosition, lastGroundPlanePosition);
        float rotationDrift = Quaternion.Angle(currentGroundRotation, lastGroundPlaneRotation);
        
        if (positionDrift > stabilityThreshold || rotationDrift > 5f)
        {
            Debug.Log($"[Stability] Drift detected - Position: {positionDrift:F3}, Rotation: {rotationDrift:F1}°");
            CorrectModelDrift(currentGroundPosition, currentGroundRotation);
            hasUnsavedAnchorData = true;
        }
        
        foreach (var modelData in loadedModels.Values)
        {
            SmoothModelToAnchor(modelData);
        }
        
        if (frameCounter % anchorUpdateFrames == 0)
        {
            lastGroundPlanePosition = currentGroundPosition;
            lastGroundPlaneRotation = currentGroundRotation;
        }
    }
    
    void CorrectModelDrift(Vector3 newGroundPosition, Quaternion newGroundRotation)
    {
        Vector3 positionDelta = newGroundPosition - lastGroundPlanePosition;
        Quaternion rotationDelta = newGroundRotation * Quaternion.Inverse(lastGroundPlaneRotation);
        
        foreach (var kvp in loadedModels)
        {
            StableModelData modelData = kvp.Value;
            
            Vector3 correctedPosition = modelData.anchorPosition + positionDelta;
            correctedPosition = rotationDelta * (correctedPosition - newGroundPosition) + newGroundPosition;
            
            modelData.UpdateAnchor(correctedPosition, rotationDelta * modelData.anchorRotation);
        }
    }
    
    void SmoothModelToAnchor(StableModelData modelData)
    {
        if (modelData.modelObject == null) return;
        
        Vector3 currentPos = modelData.modelObject.transform.position;
        Vector3 targetPos = modelData.targetPosition;
        
        Vector3 smoothedPos = Vector3.SmoothDamp(
            currentPos, 
            targetPos, 
            ref modelData.velocity, 
            1f / smoothingSpeed
        );
        
        modelData.modelObject.transform.position = smoothedPos;
        modelData.UpdateStability(stabilityThreshold);
        
        Rigidbody rb = modelData.modelObject.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.isKinematic = !modelData.isStable;
        }
    }
    
    // ============== VUFORIA EVENTS ==============
    void OnGroundPlaneDetected(HitTestResult result)
    {
        if (!groundPlaneInitialized)
        {
            Debug.Log("[Vuforia] Ground plane detected");
            groundPlaneInitialized = true;
            hasValidAnchor = true;
            
            if (lastGroundPlanePosition == Vector3.zero)
            {
                lastGroundPlanePosition = result.Position;
                lastGroundPlaneRotation = groundPlaneStage.transform.rotation;
            }
            
            PlaceLoadedModelsOnGroundPlane(lastGroundPlanePosition);
            hasUnsavedAnchorData = true;
        }
    }
    
    void OnInteractiveGroundPlaneHit(HitTestResult result)
    {
        Debug.Log($"[Vuforia] Interactive hit at: {result.Position}");
        PlaceNextModelAtPosition(result.Position);
        hasUnsavedAnchorData = true;
    }
    
    void PlaceLoadedModelsOnGroundPlane(Vector3 groundPlanePosition)
    {
        int modelIndex = 0;
        float spacing = 2f;
        
        foreach (var kvp in loadedModels)
        {
            StableModelData modelData = kvp.Value;
            
            float angle = (modelIndex * 360f / loadedModels.Count) * Mathf.Deg2Rad;
            Vector3 offset = new Vector3(Mathf.Cos(angle) * spacing, 0, Mathf.Sin(angle) * spacing);
            Vector3 targetPosition = groundPlanePosition + offset;
            
            modelData.modelObject.transform.position = targetPosition;
            modelData.UpdateAnchor(targetPosition, modelData.modelObject.transform.rotation);
            modelData.modelObject.SetActive(true);
            
            modelIndex++;
        }
    }
    
    void PlaceNextModelAtPosition(Vector3 position)
    {
        var modelList = new List<StableModelData>(loadedModels.Values);
        
        if (modelList.Count > 0)
        {
            StableModelData modelToPlace = modelList[currentModelIndex % modelList.Count];
            modelToPlace.modelObject.transform.position = position;
            modelToPlace.UpdateAnchor(position, modelToPlace.modelObject.transform.rotation);
            modelToPlace.modelObject.SetActive(true);
            
            currentModelIndex++;
        }
    }
    
    // ============== MODEL LOADING (KEEP YOUR EXISTING METHODS) ==============
    IEnumerator ProcessModelQueue()
    {
        while (modelLoadQueue.Count > 0)
        {
            string modelKey = modelLoadQueue.Dequeue();
            yield return StartCoroutine(FetchModelMetadata(modelKey));
            yield return new WaitForSeconds(1f);
        }
    }
    
    IEnumerator FetchModelMetadata(string key)
    {
        string url = apiBaseUrl + "/get/" + key;
        UnityWebRequest request = UnityWebRequest.Get(url);
        yield return request.SendWebRequest();
        
        if (request.result == UnityWebRequest.Result.Success)
        {
            try
            {
                RedisMetadataResponse response = JsonUtility.FromJson<RedisMetadataResponse>(request.downloadHandler.text);
                
                if (response.success && response.value != null)
                {
                    yield return StartCoroutine(DownloadGLBWithMetadata(key, response.value));
                }
                else
                {
                    CreateFallbackModel(key);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[AWS] JSON parsing error: {e.Message}");
                CreateFallbackModel(key);
            }
        }
        else
        {
            CreateFallbackModel(key);
        }
    }
    
    IEnumerator DownloadGLBWithMetadata(string modelKey, Metadata metadata)
    {
        UnityWebRequest www = UnityWebRequest.Get(metadata.s3_path);
        yield return www.SendWebRequest();
        
        if (www.result == UnityWebRequest.Result.Success)
        {
            yield return StartCoroutine(LoadGLBModel(modelKey, www.downloadHandler.data, metadata));
        }
        else
        {
            CreateFallbackModel(modelKey);
        }
    }
    
    IEnumerator LoadGLBModel(string modelKey, byte[] glbData, Metadata metadata)
    {
        GameObject modelParent = new GameObject($"StableAWSModel_{modelKey}");
        
        if (groundPlaneStage != null)
        {
            modelParent.transform.SetParent(groundPlaneStage.transform);
        }
        
        var gltf = new GltfImport();
        var loadTask = gltf.Load(glbData);
        yield return new WaitUntil(() => loadTask.IsCompleted);
        
        if (loadTask.Result)
        {
            var instantiateTask = gltf.InstantiateMainSceneAsync(modelParent.transform);
            yield return new WaitUntil(() => instantiateTask.IsCompleted);

            ApplyModelMetadata(modelParent, metadata);
            
            if (addPhysicsComponents)
            {
                EnsurePhysicsComponents(modelParent);
            }
            
            StableModelData stableData = new StableModelData(modelParent);
            loadedModels[modelKey] = stableData;
            
            if (!groundPlaneInitialized)
            {
                modelParent.SetActive(false);
            }
        }
        else
        {
            Destroy(modelParent);
            CreateFallbackModel(modelKey);
        }
    }
    
    void CreateFallbackModel(string modelKey)
    {
        GameObject fallbackModel = GameObject.CreatePrimitive(PrimitiveType.Cube);
        fallbackModel.name = $"Fallback_{modelKey}";
        
        var renderer = fallbackModel.GetComponent<Renderer>();
        renderer.material.color = new Color(Random.Range(0.3f, 1f), Random.Range(0.3f, 1f), Random.Range(0.3f, 1f));
        
        if (groundPlaneStage != null)
        {
            fallbackModel.transform.SetParent(groundPlaneStage.transform);
        }
        
        fallbackModel.transform.localScale = Vector3.one * modelScale;
        
        if (addPhysicsComponents)
        {
            EnsurePhysicsComponents(fallbackModel);
        }
        
        StableModelData stableData = new StableModelData(fallbackModel);
        loadedModels[modelKey] = stableData;
        
        if (!groundPlaneInitialized)
        {
            fallbackModel.SetActive(false);
        }
    }
    
    void ApplyModelMetadata(GameObject model, Metadata metadata)
    {
        float scale = modelScale;
        if (!string.IsNullOrEmpty(metadata.scale))
        {
            if (float.TryParse(metadata.scale, out float metadataScale))
            {
                scale = metadataScale;
            }
        }
        model.transform.localScale = Vector3.one * scale;
        
        if (!string.IsNullOrEmpty(metadata.rotation))
        {
            string[] rotationParts = metadata.rotation.Split(',');
            if (rotationParts.Length >= 3)
            {
                if (float.TryParse(rotationParts[0], out float x) &&
                    float.TryParse(rotationParts[1], out float y) &&
                    float.TryParse(rotationParts[2], out float z))
                {
                    model.transform.localRotation = Quaternion.Euler(x, y, z);
                }
            }
        }
    }
    
    void EnsurePhysicsComponents(GameObject modelObject)
    {
        if (!addPhysicsComponents) return;
        
        MeshRenderer[] renderers = modelObject.GetComponentsInChildren<MeshRenderer>();
        bool hasAddedRigidbody = false;
        
        foreach (MeshRenderer renderer in renderers)
        {
            GameObject obj = renderer.gameObject;
            MeshFilter meshFilter = obj.GetComponent<MeshFilter>();
            
            if (meshFilter != null && meshFilter.mesh != null)
            {
                MeshCollider meshCollider = obj.GetComponent<MeshCollider>();
                if (meshCollider == null)
                {
                    meshCollider = obj.AddComponent<MeshCollider>();
                    meshCollider.sharedMesh = meshFilter.mesh;
                    meshCollider.convex = makeCollidersConvex;
                    
                    if (defaultPhysicMaterial != null)
                    {
                        meshCollider.material = defaultPhysicMaterial;
                    }
                }
                
                if (!hasAddedRigidbody && obj.transform.parent == modelObject.transform)
                {
                    Rigidbody rb = modelObject.GetComponent<Rigidbody>();
                    if (rb == null)
                    {
                        rb = modelObject.AddComponent<Rigidbody>();
                        rb.mass = defaultMass;
                        rb.useGravity = enableGravity;
                        rb.isKinematic = false;
                        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                    }
                    hasAddedRigidbody = true;
                }
            }
        }
    }
    
    void MonitorPerformance()
    {
        deltaTime += (Time.unscaledDeltaTime - deltaTime) * 0.1f;
    }
    
    void OnDestroy()
    {
        CleanupOnExit();
    }
    
    // ============== DATA CLASSES ==============
    [System.Serializable]
    public class LocalAnchorData
    {
        public string sessionId;
        public string timestamp;
        public Vector3 groundPlanePosition;
        public Quaternion groundPlaneRotation;
        public List<LocalModelPosition> modelPositions;
    }
    
    [System.Serializable]
    public class LocalModelPosition
    {
        public string modelKey;
        public Vector3 position;
        public Quaternion rotation;
        public bool isStable;
    }
    
    [System.Serializable]
    public class RedisMetadataResponse
    {
        public bool success;
        public string key;
        public Metadata value;
    }

    [System.Serializable]
    public class Metadata
    {
        public string s3_path;
        public string version;
        public string scale;
        public string rotation;
        public string description;
    }
    
    // ============== DEBUG GUI ==============
    #if UNITY_EDITOR
    void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 10, 350, 300));
        
        GUILayout.Label($"Local AR Anchor Cache System");
        GUILayout.Label($"Ground Plane: {(groundPlaneInitialized ? "Ready" : "Waiting...")}");
        GUILayout.Label($"Models: {loadedModels.Count}");
        GUILayout.Label($"FPS: {(1.0f / deltaTime):F1}");
        GUILayout.Label($"Cache: {(enableLocalAnchorCache ? "Enabled" : "Disabled")}");
        GUILayout.Label($"Unsaved: {hasUnsavedAnchorData}");
        GUILayout.Label($"{GetCacheInfo()}");
        
        GUILayout.Space(10);
        
        if (GUILayout.Button("Force Save Cache"))
        {
            ForceSaveAnchors();
        }
        
        if (GUILayout.Button("Clear Cache"))
        {
            ClearCachedAnchors();
        }
        
        if (GUILayout.Button("Delete Cache on Exit: " + (deleteOnAppExit ? "ON" : "OFF")))
        {
            deleteOnAppExit = !deleteOnAppExit;
        }
        
        GUILayout.EndArea();
    }
    #endif
}
