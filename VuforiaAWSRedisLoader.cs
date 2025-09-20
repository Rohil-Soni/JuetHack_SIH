using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using Vuforia;
using GLTFast;
using UnityEngine.Networking;
using System.IO;

/// <summary>
/// FULLY FIXED NUCLEAR VERSION: Complete Vuforia + AWS Redis + S3 with Build-Safe API Control
/// All compiler errors resolved, all features included
/// </summary>
public class VuforiaAWSRedisLoader : MonoBehaviour
{
    [Header("=== BUILD MODE CONTROL ===")]
    public bool enableAPICallsInEditor = false; // UNCHECK THIS TO BUILD SUCCESSFULLY
    
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
    public float autoSaveInterval = 10f;
    public bool deleteOnAppExit = true;
    
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
    
    // ============== BUILD-SAFE API CONTROL ==============
    bool ShouldMakeAPICalls()
    {
        #if UNITY_EDITOR
        return enableAPICallsInEditor && Application.isPlaying;
        #else
        return true; // ALWAYS ENABLED IN BUILT APP
        #endif
    }
    
    // ============== SYSTEM INITIALIZATION ==============
    void InitializeSystem()
    {
        Application.targetFrameRate = 60;
        Debug.Log($"[AWS Redis] Initializing - API Calls: {(ShouldMakeAPICalls() ? "ENABLED" : "BUILD-SAFE MODE")}");
        
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
    
    // ============== SMART MODEL LOADING WITH BUILD PROTECTION ==============
    IEnumerator ProcessModelQueue()
    {
        while (modelLoadQueue.Count > 0)
        {
            string modelKey = modelLoadQueue.Dequeue();
            Debug.Log($"[AWS] Processing model: {modelKey}");
            
            if (ShouldMakeAPICalls())
            {
                // Check network connectivity first
                if (Application.internetReachability == NetworkReachability.NotReachable)
                {
                    Debug.LogWarning($"[AWS] No internet connection - creating fallback for {modelKey}");
                    CreateFallbackModel(modelKey);
                }
                else
                {
                    yield return StartCoroutine(FetchModelMetadata(modelKey));
                }
            }
            else
            {
                Debug.Log($"[BUILD MODE] Skipping API call - creating fallback for {modelKey}");
                CreateFallbackModel(modelKey);
            }
            
            yield return new WaitForSeconds(0.5f);
        }
        
        Debug.Log("[AWS] All models processed from queue");
    }
    
    IEnumerator FetchModelMetadata(string key)
    {
        string url = apiBaseUrl + "/get/" + key;
        
        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            // Short timeout to prevent hanging during builds
            request.timeout = 8;
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("User-Agent", "Unity-AR-App");
            
            Debug.Log($"[AWS] Fetching: {url}");
            yield return request.SendWebRequest();
            
            if (request.result == UnityWebRequest.Result.Success)
            {
                Debug.Log("✅ Metadata response: " + request.downloadHandler.text);
                
                Metadata metadataToDownload = null;

                // Parse JSON - NO yield inside try-catch
                bool parseSuccess = false;
                try
                {
                    RedisMetadataResponse response = JsonUtility.FromJson<RedisMetadataResponse>(request.downloadHandler.text);
                    
                    if (response.success && response.value != null)
                    {
                        Debug.Log("📦 S3 Path: " + response.value.s3_path);
                        metadataToDownload = response.value;
                        parseSuccess = true;
                    }
                    else
                    {
                        Debug.LogWarning($"[AWS] API returned failure for {key}");
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[AWS] JSON parsing error: {e.Message}");
                }

                // Handle result OUTSIDE try-catch
                if (parseSuccess && metadataToDownload != null)
                {
                    yield return StartCoroutine(DownloadGLBWithMetadata(key, metadataToDownload));
                }
                else
                {
                    CreateFallbackModel(key);
                }
            }
            else
            {
                Debug.LogError($"❌ API Error for {key}: {request.error} (Code: {request.responseCode})");
                CreateFallbackModel(key);
            }
        }
    }
    
    IEnumerator DownloadGLBWithMetadata(string modelKey, Metadata metadata)
    {
        using (UnityWebRequest www = UnityWebRequest.Get(metadata.s3_path))
        {
            www.timeout = 20;
            
            Debug.Log($"[S3] Downloading: {metadata.s3_path}");
            yield return www.SendWebRequest();
            
            if (www.result == UnityWebRequest.Result.Success)
            {
                Debug.Log("✅ Downloaded GLB, size: " + www.downloadHandler.data.Length);
                yield return StartCoroutine(LoadGLBModel(modelKey, www.downloadHandler.data, metadata));
            }
            else
            {
                Debug.LogError($"❌ S3 Download Error: {www.error}");
                CreateFallbackModel(modelKey);
            }
        }
    }
    
    // ============== FIXED GLB LOADING - NO TRY-CATCH WITH YIELD ==============
    IEnumerator LoadGLBModel(string modelKey, byte[] glbData, Metadata metadata)
    {
        GameObject modelParent = new GameObject($"StableAWSModel_{modelKey}");
        
        if (groundPlaneStage != null)
        {
            modelParent.transform.SetParent(groundPlaneStage.transform);
        }
        
        var gltf = new GltfImport();
        
        // Start the loading task OUTSIDE try-catch
        var loadTask = gltf.Load(glbData);
        
        // Wait for the task to complete (OUTSIDE try-catch)
        yield return new WaitUntil(() => loadTask.IsCompleted);
        
        // Check for exceptions AFTER the yield
        if (loadTask.IsFaulted)
        {
            Debug.LogError($"❌ GLB loading exception: {(loadTask.Exception?.InnerException?.Message ?? "Unknown error")}");
            Destroy(modelParent);
            CreateFallbackModel(modelKey);
            yield break;
        }
        
        // Check if loading was successful
        if (loadTask.Result)
        {
            // Start the instantiation task OUTSIDE try-catch
            var instantiateTask = gltf.InstantiateMainSceneAsync(modelParent.transform);
            
            // Wait for instantiation to complete (OUTSIDE try-catch)
            yield return new WaitUntil(() => instantiateTask.IsCompleted);

            if (instantiateTask.IsFaulted)
            {
                Debug.LogError($"❌ GLB instantiation exception: {(instantiateTask.Exception?.InnerException?.Message ?? "Unknown error")}");
                Destroy(modelParent);
                CreateFallbackModel(modelKey);
                yield break;
            }

            Debug.Log("🎉 Model loaded into scene!");
            
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
            
            Debug.Log($"[AWS] Stable model configured: {modelKey}");
        }
        else
        {
            Debug.LogError("❌ Failed to load GLB binary data");
            Destroy(modelParent);
            CreateFallbackModel(modelKey);
        }
    }
    
    void CreateFallbackModel(string modelKey)
    {
        GameObject fallbackModel;
        
        // Use prefab if available, otherwise create colorful cube
        if (fallbackPrefabs.Length > 0)
        {
            int prefabIndex = Mathf.Abs(modelKey.GetHashCode()) % fallbackPrefabs.Length;
            fallbackModel = Instantiate(fallbackPrefabs[prefabIndex]);
            fallbackModel.name = $"StableFallback_{modelKey}";
        }
        else
        {
            fallbackModel = GameObject.CreatePrimitive(PrimitiveType.Cube);
            fallbackModel.name = $"StableFallback_{modelKey}";
            
            // Make it rainbow so you know it's working
            var renderer = fallbackModel.GetComponent<Renderer>();
            renderer.material.color = new Color(Random.Range(0.3f, 1f), Random.Range(0.3f, 1f), Random.Range(0.3f, 1f));
        }
        
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
        
        Debug.Log($"[Fallback] Created stable model for {modelKey}");
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
        
        Debug.Log($"[Physics] Added components to stable {modelObject.name}");
    }
    
    // ============== COMPLETE STABILITY SYSTEM ==============
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
            
            Debug.Log($"[Stability] Corrected drift for {kvp.Key}");
        }
    }
    
    void SmoothModelToAnchor(StableModelData modelData)
    {
        if (modelData.modelObject == null) return;
        
        Vector3 currentPos = modelData.modelObject.transform.position;
        Vector3 targetPos = modelData.targetPosition;
        
        Vector3 smoothedPos = Vector3.SmoothDamp(currentPos, targetPos, ref modelData.velocity, 1f / smoothingSpeed);
        modelData.modelObject.transform.position = smoothedPos;
        modelData.UpdateStability(stabilityThreshold);
        
        Rigidbody rb = modelData.modelObject.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.isKinematic = !modelData.isStable;
        }
    }
    
    // ============== VUFORIA EVENTS WITH STABILITY ==============
    void OnGroundPlaneDetected(HitTestResult result)
    {
        if (!groundPlaneInitialized)
        {
            Debug.Log("[Vuforia] Ground plane detected - initializing stable anchoring");
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
            
            Debug.Log($"[Placement] Stable placement for {kvp.Key}");
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
            Debug.Log($"[Interactive] Placed stable model at {position}");
        }
    }
    
    void MonitorPerformance()
    {
        deltaTime += (Time.unscaledDeltaTime - deltaTime) * 0.1f;
    }
    
    // ============== COMPLETE LOCAL ANCHOR CACHING ==============
    void InitializeLocalAnchorCache()
    {
        sessionId = System.Guid.NewGuid().ToString().Substring(0, 8);
        string cacheFileName = $"ar_anchors_{sessionId}.json";
        anchorCacheFilePath = Path.Combine(Application.persistentDataPath, cacheFileName);
        
        Debug.Log($"[Local Cache] Initialized - File: {anchorCacheFilePath}");
        
        LoadLocalAnchorCache();
        
        if (autoSaveCoroutine == null)
        {
            autoSaveCoroutine = StartCoroutine(AutoSaveAnchors());
        }
        
        RegisterCleanupEvents();
    }
    
    void RegisterCleanupEvents()
    {
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
                Debug.Log($"✅ [Local Cache] Loaded stable anchor data from file");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Local Cache] Failed to load: {e.Message}");
            if (File.Exists(anchorCacheFilePath))
            {
                File.Delete(anchorCacheFilePath);
            }
        }
    }
    
    void ApplyLocalAnchorData(LocalAnchorData anchorData)
    {
        if (anchorData.groundPlanePosition != Vector3.zero)
        {
            lastGroundPlanePosition = anchorData.groundPlanePosition;
            lastGroundPlaneRotation = anchorData.groundPlaneRotation;
            hasValidAnchor = true;
            
            Debug.Log($"[Local Cache] Restored stable ground plane: {anchorData.groundPlanePosition}");
        }
        
        Debug.Log($"[Local Cache] Applied {anchorData.modelPositions.Count} cached stable positions");
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
            Debug.Log($"💾 [Local Cache] Saved {anchorData.modelPositions.Count} stable model anchors");
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
    
    void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus && enableLocalAnchorCache)
        {
            Debug.Log("[Local Cache] App lost focus - saving stable anchors");
            SaveLocalAnchorCache();
        }
    }
    
    void OnApplicationQuitting()
    {
        CleanupOnExit();
    }
    
    void CleanupOnExit()
    {
        Debug.Log("[Local Cache] App exiting - cleaning up stable anchor data");
        
        if (deleteOnAppExit && enableLocalAnchorCache)
        {
            DeleteLocalAnchorCache();
        }
        else if (enableLocalAnchorCache)
        {
            SaveLocalAnchorCache();
        }
        
        if (autoSaveCoroutine != null)
        {
            StopCoroutine(autoSaveCoroutine);
            autoSaveCoroutine = null;
        }
        
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
                Debug.Log("🗑️ [Local Cache] Deleted stable anchor cache file on exit");
            }
            
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
    
    // ============== PUBLIC API METHODS ==============
    public void ForceSaveAnchors()
    {
        if (enableLocalAnchorCache)
        {
            hasUnsavedAnchorData = true;
            SaveLocalAnchorCache();
            Debug.Log("[Local Cache] Force saved stable anchor data");
        }
    }
    
    public void ClearCachedAnchors()
    {
        if (enableLocalAnchorCache)
        {
            DeleteLocalAnchorCache();
            hasValidAnchor = false;
            lastGroundPlanePosition = Vector3.zero;
            Debug.Log("[Local Cache] Manually cleared cached stable anchors");
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
    
    public void LoadSpecificModel(string modelKey)
    {
        if (!loadedModels.ContainsKey(modelKey))
        {
            modelLoadQueue.Enqueue(modelKey);
            StartCoroutine(ProcessModelQueue());
        }
    }
    
    public void ToggleModelVisibility(string modelKey)
    {
        if (loadedModels.ContainsKey(modelKey))
        {
            GameObject model = loadedModels[modelKey].modelObject;
            model.SetActive(!model.activeInHierarchy);
        }
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
        public List<LocalModelPosition> modelPositions = new List<LocalModelPosition>();
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
        GUILayout.BeginArea(new Rect(10, 10, 350, 280));
        
        GUILayout.Label($"FULLY FIXED AR Model Loader");
        GUILayout.Label($"API Calls: {(ShouldMakeAPICalls() ? "ENABLED" : "BUILD-SAFE")}");
        GUILayout.Label($"Ground Plane: {(groundPlaneInitialized ? "Ready" : "Waiting...")}");
        GUILayout.Label($"Stable Models: {loadedModels.Count}");
        GUILayout.Label($"FPS: {(1.0f / deltaTime):F1}");
        GUILayout.Label($"Internet: {Application.internetReachability}");
        GUILayout.Label($"Cache: {(enableLocalAnchorCache ? "Enabled" : "Disabled")}");
        GUILayout.Label($"Unsaved: {hasUnsavedAnchorData}");
        GUILayout.Label($"{GetCacheInfo()}");
        
        GUILayout.Space(10);
        
        if (GUILayout.Button("Toggle API Calls"))
        {
            enableAPICallsInEditor = !enableAPICallsInEditor;
        }
        
        if (GUILayout.Button("Test temple1"))
        {
            LoadSpecificModel("model:temple1");
        }
        
        if (GUILayout.Button("Force Save Cache"))
        {
            ForceSaveAnchors();
        }
        
        if (GUILayout.Button("Clear Cache"))
        {
            ClearCachedAnchors();
        }
        
        GUILayout.EndArea();
    }
    #endif
}
