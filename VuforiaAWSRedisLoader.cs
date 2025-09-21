using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using Vuforia;
using GLTFast;
using UnityEngine.Networking;
using System.IO;
using System.Net;

/// <summary>
/// HERITAGE AR - CORRECTED VERSION with HTTPS + API Key + Proper Scaling
/// Fixed based on Parnika's feedback
/// </summary>
public class VuforiaAWSRedisLoader : MonoBehaviour
{
    [Header("=== BUILD MODE CONTROL ===")]
    public bool enableAPICallsInEditor = false; // UNCHECK THIS TO BUILD SUCCESSFULLY
    
    [Header("=== AWS REDIS CONFIGURATION ===")]
    public string apiBaseUrl = "https://heritagear.duckdns.org"; // FIXED - HTTPS + Domain
    public string apiKey = "temple@1234"; // API KEY FOR AUTHENTICATION
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
    public float modelScale = 1f; // BASE SCALE - Redis metadata will override this
    public GameObject[] fallbackPrefabs;
    
    [Header("=== DEBUG TESTING ===")]
    public bool forceTestCubes = false;
    
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
        // Android HTTPS fix
        #if UNITY_ANDROID && !UNITY_EDITOR
        System.Net.ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
        Debug.Log("[Android Fix] Enabled HTTPS certificate bypass");
        #endif
        
        if (forceTestCubes)
        {
            CreateTestModels();
            return;
        }
        
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
    
    // ============== DEBUG TEST CUBES ==============
    void CreateTestModels()
    {
        Debug.Log("[TEST MODE] Creating test cubes instead of loading models");
        
        for (int i = 0; i < 3; i++)
        {
            GameObject testCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            testCube.name = $"TestCube_{i}";
            testCube.transform.position = new Vector3(i * 3f, 1f, 5f);
            testCube.transform.localScale = Vector3.one * modelScale;
            
            var renderer = testCube.GetComponent<Renderer>();
            renderer.material.color = i == 0 ? Color.red : i == 1 ? Color.green : Color.blue;
            
            Debug.Log($"Created test cube {i} at position {testCube.transform.position} with scale {modelScale}");
        }
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
        Debug.Log($"[Heritage AR] Initializing - API Calls: {(ShouldMakeAPICalls() ? "ENABLED" : "BUILD-SAFE MODE")}");
        Debug.Log($"[Heritage AR] API URL: {apiBaseUrl}");
        Debug.Log($"[Heritage AR] Model Scale: {modelScale}");
        
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
    
    // ============== SMART MODEL LOADING ==============
    IEnumerator ProcessModelQueue()
    {
        while (modelLoadQueue.Count > 0)
        {
            string modelKey = modelLoadQueue.Dequeue();
            Debug.Log($"[Heritage AR] Processing model: {modelKey}");
            
            if (ShouldMakeAPICalls())
            {
                if (Application.internetReachability == NetworkReachability.NotReachable)
                {
                    Debug.LogWarning($"[Heritage AR] No internet connection - creating fallback for {modelKey}");
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
        
        Debug.Log("[Heritage AR] All models processed from queue");
    }
    
    // ============== API DEBUG METHODS ==============
    public void DebugAPIStatus()
    {
        Debug.Log($"API Calls Enabled: {ShouldMakeAPICalls()}");
        Debug.Log($"Internet: {Application.internetReachability}");
        Debug.Log($"API URL: {apiBaseUrl}");
        Debug.Log($"API Key: {apiKey}");
        
        StartCoroutine(TestAPI());
    }

    IEnumerator TestAPI()
    {
        string testUrl = apiBaseUrl + "/get/model:temple1";
        using (UnityWebRequest request = UnityWebRequest.Get(testUrl))
        {
            // FIXED - ADD API KEY HEADER
            request.SetRequestHeader("X-API-Key", apiKey);
            yield return request.SendWebRequest();
            
            if (request.result == UnityWebRequest.Result.Success)
            {
                Debug.Log("✅ Heritage AR API Working: " + request.downloadHandler.text);
            }
            else
            {
                Debug.Log("❌ Heritage AR API Failed: " + request.error);
                Debug.Log("❌ Response Code: " + request.responseCode);
            }
        }
    }
    
    // ============== CORRECTED API FETCH WITH HTTPS + API KEY ==============
    IEnumerator FetchModelMetadata(string key)
    {
        string url = apiBaseUrl + "/get/" + key;
        
        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            // Enhanced headers with API KEY
            request.timeout = 15; // Increased for HTTPS
            request.SetRequestHeader("User-Agent", "Unity-HeritageAR-App/1.0");
            request.SetRequestHeader("Accept", "application/json");
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("X-API-Key", apiKey); // FIXED - API KEY ADDED
            
            // HTTPS certificate handler for Android
            #if UNITY_ANDROID && !UNITY_EDITOR
            request.certificateHandler = new AcceptAllCertificatesHandler();
            #endif
            
            Debug.Log($"[Heritage AR] Requesting: {url}");
            Debug.Log($"[Heritage AR] Using API Key: {apiKey}");
            Debug.Log($"[Heritage AR] Internet Status: {Application.internetReachability}");
            
            yield return request.SendWebRequest();
            
            Debug.Log($"[Heritage AR] Response Code: {request.responseCode}");
            Debug.Log($"[Heritage AR] Result: {request.result}");
            if (!string.IsNullOrEmpty(request.error))
            {
                Debug.Log($"[Heritage AR] Error Details: {request.error}");
            }
            
            if (request.result == UnityWebRequest.Result.Success)
            {
                Debug.Log("✅ [Heritage AR] Raw Response: " + request.downloadHandler.text);
                
                Metadata metadataToDownload = null;
                bool parseSuccess = false;
                
                try
                {
                    RedisMetadataResponse response = JsonUtility.FromJson<RedisMetadataResponse>(request.downloadHandler.text);
                    
                    if (response.success && response.value != null)
                    {
                        Debug.Log("📦 S3 Path: " + response.value.s3_path);
                        Debug.Log($"📏 Scale from Redis: {response.value.scale}");
                        metadataToDownload = response.value;
                        parseSuccess = true;
                    }
                    else
                    {
                        Debug.LogWarning($"[Heritage AR] API returned failure for {key}");
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[Heritage AR] JSON parsing error: {e.Message}");
                    Debug.LogError($"[Heritage AR] Raw response was: {request.downloadHandler.text}");
                }

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
                Debug.LogError($"❌ [Heritage AR] API failed for {key}");
                Debug.LogError($"   Response Code: {request.responseCode}");
                Debug.LogError($"   Error: {request.error}");
                Debug.LogError($"   URL: {url}");
                CreateFallbackModel(key);
            }
        }
    }
    
    // ============== GLB DOWNLOAD WITH HTTPS SUPPORT ==============
    IEnumerator DownloadGLBWithMetadata(string modelKey, Metadata metadata)
    {
        using (UnityWebRequest www = UnityWebRequest.Get(metadata.s3_path))
        {
            www.timeout = 60;
            
            #if UNITY_ANDROID && !UNITY_EDITOR
            www.certificateHandler = new AcceptAllCertificatesHandler();
            #endif
            
            www.downloadHandler = new DownloadHandlerBuffer();
            
            Debug.Log($"[S3] Starting download: {metadata.s3_path}");
            
            var operation = www.SendWebRequest();
            
            while (!operation.isDone)
            {
                float progress = www.downloadProgress;
                if (progress > 0)
                {
                    Debug.Log($"[S3] Download progress: {progress:P2}");
                }
                yield return null;
            }
            
            if (www.result == UnityWebRequest.Result.Success)
            {
                byte[] data = www.downloadHandler.data; // FIXED - NO MARKDOWN LINK
                Debug.Log($"✅ Downloaded GLB successfully - Size: {data.Length} bytes");
                
                if (data.Length > 0 && IsValidGLBData(data))
                {
                    yield return StartCoroutine(LoadGLBModel(modelKey, data, metadata));
                }
                else
                {
                    Debug.LogError("❌ Downloaded GLB data is corrupted or invalid");
                    CreateFallbackModel(modelKey);
                }
            }
            else
            {
                Debug.LogError($"❌ S3 Download Error: {www.error}");
                Debug.LogError($"❌ Response Code: {www.responseCode}");
                CreateFallbackModel(modelKey);
            }
        }
    }
    
    bool IsValidGLBData(byte[] data)
    {
        if (data.Length < 12) return false;
        
        string magic = System.Text.Encoding.ASCII.GetString(data, 0, 4);
        return magic == "glTF";
    }
    
    // ============== GLB LOADING ==============
    IEnumerator LoadGLBModel(string modelKey, byte[] glbData, Metadata metadata)
    {
        GameObject modelParent = new GameObject($"HeritageAR_{modelKey}");
        
        if (groundPlaneStage != null)
        {
            modelParent.transform.SetParent(groundPlaneStage.transform);
        }
        
        System.GC.Collect();
        
        var gltf = new GltfImport();
        
        Debug.Log($"[GLB] Starting GLB import for {modelKey} - Size: {glbData.Length} bytes");
        
        var loadTask = gltf.Load(glbData);
        
        float timeout = 30f;
        float elapsed = 0f;
        
        while (!loadTask.IsCompleted && elapsed < timeout)
        {
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }
        
        if (elapsed >= timeout)
        {
            Debug.LogError($"❌ GLB loading timeout for {modelKey}");
            Destroy(modelParent);
            CreateFallbackModel(modelKey);
            yield break;
        }
        
        if (loadTask.IsFaulted)
        {
            Debug.LogError($"❌ GLB loading exception: {(loadTask.Exception?.InnerException?.Message ?? "Unknown error")}");
            Destroy(modelParent);
            CreateFallbackModel(modelKey);
            yield break;
        }
        
        if (loadTask.Result)
        {
            Debug.Log($"✅ GLB loaded successfully, instantiating {modelKey}");
            
            var instantiateTask = gltf.InstantiateMainSceneAsync(modelParent.transform);
            
            yield return new WaitUntil(() => instantiateTask.IsCompleted);

            if (instantiateTask.IsFaulted)
            {
                Debug.LogError($"❌ GLB instantiation exception: {(instantiateTask.Exception?.InnerException?.Message ?? "Unknown error")}");
                Destroy(modelParent);
                CreateFallbackModel(modelKey);
                yield break;
            }

            Debug.Log("🎉 Heritage AR Model loaded and instantiated successfully!");
            
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
            
            Debug.Log($"[Heritage AR] Model {modelKey} configured and ready");
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
        
        if (fallbackPrefabs.Length > 0)
        {
            int prefabIndex = Mathf.Abs(modelKey.GetHashCode()) % fallbackPrefabs.Length;
            fallbackModel = Instantiate(fallbackPrefabs[prefabIndex]);
            fallbackModel.name = $"HeritageFallback_{modelKey}";
        }
        else
        {
            fallbackModel = GameObject.CreatePrimitive(PrimitiveType.Cube);
            fallbackModel.name = $"HeritageFallback_{modelKey}";
            
            var renderer = fallbackModel.GetComponent<Renderer>();
            renderer.material.color = new Color(Random.Range(0.3f, 1f), Random.Range(0.3f, 1f), Random.Range(0.3f, 1f));
        }
        
        if (groundPlaneStage != null)
        {
            fallbackModel.transform.SetParent(groundPlaneStage.transform);
        }
        
        // FIXED - RESPECT BASE modelScale SETTING
        float scale = modelScale; // Use base scale for fallbacks
        fallbackModel.transform.localScale = Vector3.one * scale;
        
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
        
        Debug.Log($"[Fallback] Created Heritage AR fallback for {modelKey} with scale {scale}");
    }
    
    // ============== FIXED - RESPECT REDIS METADATA SCALE ==============
    void ApplyModelMetadata(GameObject model, Metadata metadata)
    {
        // FIXED - Start with base scale, then respect Redis metadata
        float scale = modelScale; // Default base scale
        
        if (!string.IsNullOrEmpty(metadata.scale))
        {
            if (float.TryParse(metadata.scale, out float metadataScale))
            {
                scale = metadataScale; // Respect Redis metadata scale
                Debug.Log($"[Heritage AR] Using Redis scale: {metadataScale} for {model.name}");
            }
        }
        
        model.transform.localScale = Vector3.one * scale;
        Debug.Log($"[Model Scale] Applied scale {scale} to {model.name}");
        
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
                    Debug.Log($"[Heritage AR] Applied rotation: {x},{y},{z} to {model.name}");
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
    
    // ============== STABILITY SYSTEM ==============
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
        
        Vector3 smoothedPos = Vector3.SmoothDamp(currentPos, targetPos, ref modelData.velocity, 1f / smoothingSpeed);
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
            Debug.Log("[Vuforia] Ground plane detected - Heritage AR ready");
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
        float spacing = 3f;
        
        foreach (var kvp in loadedModels)
        {
            StableModelData modelData = kvp.Value;
            
            float angle = (modelIndex * 360f / loadedModels.Count) * Mathf.Deg2Rad;
            Vector3 offset = new Vector3(Mathf.Cos(angle) * spacing, 0.1f, Mathf.Sin(angle) * spacing);
            Vector3 targetPosition = groundPlanePosition + offset;
            
            modelData.modelObject.transform.position = targetPosition;
            modelData.UpdateAnchor(targetPosition, modelData.modelObject.transform.rotation);
            modelData.modelObject.SetActive(true);
            
            Debug.Log($"[Placement] Heritage AR placed {kvp.Key} at {targetPosition}");
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
            Debug.Log($"[Interactive] Heritage AR placed model at {position}");
        }
    }
    
    void MonitorPerformance()
    {
        deltaTime += (Time.unscaledDeltaTime - deltaTime) * 0.1f;
    }
    
    // ============== LOCAL ANCHOR CACHING ==============
    void InitializeLocalAnchorCache()
    {
        sessionId = System.Guid.NewGuid().ToString().Substring(0, 8);
        string cacheFileName = $"heritage_ar_anchors_{sessionId}.json";
        anchorCacheFilePath = Path.Combine(Application.persistentDataPath, cacheFileName);
        
        if (autoSaveCoroutine == null)
        {
            autoSaveCoroutine = StartCoroutine(AutoSaveAnchors());
        }
        
        Application.focusChanged += OnApplicationFocus;
        Application.quitting += OnApplicationQuitting;
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
    
    void SaveLocalAnchorCache()
    {
        if (!enableLocalAnchorCache) return;
        
        try
        {
            var anchorData = new LocalAnchorData
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
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[Cache] Save failed: {e.Message}");
        }
    }
    
    void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus && enableLocalAnchorCache)
        {
            SaveLocalAnchorCache();
        }
    }
    
    void OnApplicationQuitting()
    {
        if (deleteOnAppExit && enableLocalAnchorCache)
        {
            try
            {
                if (File.Exists(anchorCacheFilePath))
                {
                    File.Delete(anchorCacheFilePath);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[Cache] Cleanup failed: {e.Message}");
            }
        }
        
        if (autoSaveCoroutine != null)
        {
            StopCoroutine(autoSaveCoroutine);
        }
    }
    
    // ============== PUBLIC API METHODS ==============
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
        OnApplicationQuitting();
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
    
    // ============== CERTIFICATE HANDLER ==============
    public class AcceptAllCertificatesHandler : CertificateHandler
    {
        protected override bool ValidateCertificate(byte[] certificateData)
        {
            return true; // Accept all HTTPS certificates for Android builds
        }
    }
    
    // ============== DEBUG GUI ==============
    #if UNITY_EDITOR
    void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 10, 450, 400));
        
        GUILayout.Label($"HERITAGE AR - Model Loader");
        GUILayout.Label($"API URL: {apiBaseUrl}");
        GUILayout.Label($"API Key: {apiKey}");
        GUILayout.Label($"API Calls: {(ShouldMakeAPICalls() ? "ENABLED" : "BUILD-SAFE")}");
        GUILayout.Label($"Ground Plane: {(groundPlaneInitialized ? "Ready" : "Waiting...")}");
        GUILayout.Label($"Models: {loadedModels.Count}");
        GUILayout.Label($"FPS: {(1.0f / deltaTime):F1}");
        GUILayout.Label($"Internet: {Application.internetReachability}");
        GUILayout.Label($"Model Scale: {modelScale}");
        
        GUILayout.Space(10);
        
        if (GUILayout.Button("Toggle API Calls"))
        {
            enableAPICallsInEditor = !enableAPICallsInEditor;
        }
        
        if (GUILayout.Button("Test Heritage AR API"))
        {
            DebugAPIStatus();
        }
        
        if (GUILayout.Button("Test temple1"))
        {
            LoadSpecificModel("model:temple1");
        }
        
        if (GUILayout.Button("Force Test Cubes"))
        {
            forceTestCubes = !forceTestCubes;
        }
        
        GUILayout.EndArea();
    }
    #endif
}
