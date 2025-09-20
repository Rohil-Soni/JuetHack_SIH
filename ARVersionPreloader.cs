using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Vuforia;

// ============== VUFORIA AR VERSION PRELOADER SCRIPT ==============
public class VuforiaARVersionPreloader : MonoBehaviour
{
    [Header("Version Management")]
    public string currentVersion = "1.0.0";
    public bool autoPreloadNextVersion = true;
    
    [Header("Vuforia Ground Plane Components")]
    public PlaneFinderBehaviour planeFinder;
    public GameObject groundPlaneStage;
    public ContentPositioningBehaviour contentPositioning;
    
    [Header("Player Movement Detection")]
    public float movementThreshold = 0.5f;
    public float movementCheckInterval = 0.1f;
    
    [Header("Chunk System")]
    public float chunkRadius = 6f;
    public int maxChunksLoaded = 150;
    public int priorityChunkRadius = 3;
    public int preloadChunksAhead = 8;
    
    [Header("Performance")]
    public int maxChunksPerFrame = 2;
    public float chunkUnloadDelay = 5f;
    
    [Header("Model Prefabs")]
    public GameObject[] modelPrefabs; // Your 3D models from Redis/S3
    
    private Vector3 lastPlayerPosition;
    private Vector3 currentPlayerPosition;
    private bool playerIsMoving = false;
    private bool groundPlaneInitialized = false;
    
    private Dictionary<Vector2, VuforiaChunkData> loadedChunks = new Dictionary<Vector2, VuforiaChunkData>();
    private Dictionary<Vector2, VuforiaChunkData> cachedChunks = new Dictionary<Vector2, VuforiaChunkData>();
    private Queue<Vector2> chunksToLoad = new Queue<Vector2>();
    private Queue<Vector2> chunksToUnload = new Queue<Vector2>();
    
    // Redis HTTP client for model loading
    private RedisHttpClient redisClient;
    
    void Start()
    {
        InitializeVuforiaVersionPreloader();
        StartCoroutine(DetectPlayerMovement());
        StartCoroutine(ProcessChunkLoading());
        
        // Initialize Redis client
        redisClient = new RedisHttpClient();
    }
    
    // ============== VUFORIA INITIALIZATION ==============
    void InitializeVuforiaVersionPreloader()
    {
        Debug.Log($"Initializing Vuforia AR Version Preloader - Current Version: {currentVersion}");
    
        // Get Vuforia components if not assigned
        if (planeFinder == null)
            planeFinder = FindObjectOfType<PlaneFinderBehaviour>();

        // Find Ground Plane Stage GameObject by name
        if (groundPlaneStage == null)
            groundPlaneStage = GameObject.Find("Ground Plane Stage");

        if (contentPositioning == null)
            contentPositioning = FindObjectOfType<ContentPositioningBehaviour>();

        // Setup Vuforia Ground Plane events
        SetupVuforiaEvents();
        
        // Set initial positions
        lastPlayerPosition = Camera.main.transform.position;
        currentPlayerPosition = Camera.main.transform.position;
        
        if (autoPreloadNextVersion)
        {
            StartCoroutine(PreloadNextVersion());
        }
    }
    
    void SetupVuforiaEvents()
    {
        if (planeFinder != null)
        {
            // Listen to automatic ground plane detection
            planeFinder.OnAutomaticHitTest.AddListener(OnGroundPlaneDetected);
            planeFinder.OnInteractiveHitTest.AddListener(OnInteractiveGroundPlaneHit);
            
            Debug.Log("[Vuforia] Ground plane event listeners registered");
        }
        else
        {
            Debug.LogError("[Vuforia] PlaneFinderBehaviour not found! Add it to your scene.");
        }
    }
    
    // ============== VUFORIA GROUND PLANE EVENTS ==============
    void OnGroundPlaneDetected(HitTestResult result)
    {
        if (!groundPlaneInitialized)
        {
            Debug.Log("[Vuforia] Ground plane detected - initializing chunk system");
            groundPlaneInitialized = true;
            
            // Set ground plane as reference point
            Vector3 groundPlanePosition = result.Position;
            transform.position = groundPlanePosition;
            
            // Load initial chunks around detected ground plane
            LoadInitialChunksOnGroundPlane(groundPlanePosition);
        }
    }
    
    void OnInteractiveGroundPlaneHit(HitTestResult result)
    {
        Debug.Log($"[Vuforia] Interactive hit at: {result.Position}");
        // Handle manual placement if needed
        PlaceChunkAtPosition(result.Position);
    }
    
    // ============== CHUNK SYSTEM WITH VUFORIA GROUND PROJECTION ==============
    void LoadInitialChunksOnGroundPlane(Vector3 groundPlaneCenter)
    {
        Vector2 centerChunk = WorldToChunkCoord(groundPlaneCenter);
        
        for (int x = -2; x <= 2; x++)
        {
            for (int z = -2; z <= 2; z++)
            {
                Vector2 chunkCoord = new Vector2(centerChunk.x + x, centerChunk.y + z);
                Vector3 chunkWorldPos = ChunkToWorldCoord(chunkCoord);
                
                // Project chunk position onto ground plane using Vuforia hit test
                StartCoroutine(LoadChunkOnGroundPlane(chunkCoord, chunkWorldPos));
            }
        }
    }
    
    IEnumerator LoadChunkOnGroundPlane(Vector2 chunkCoord, Vector3 targetPosition)
    {
        // Perform Vuforia hit test to project chunk onto ground plane
        yield return StartCoroutine(ProjectPositionToGroundPlane(targetPosition, (projectedPos, success) =>
        {
            if (success)
            {
                LoadChunkAtPosition(chunkCoord, projectedPos);
            }
            else
            {
                // Fallback to original position if projection fails
                LoadChunkAtPosition(chunkCoord, targetPosition);
            }
        }));
    }
    
    IEnumerator ProjectPositionToGroundPlane(Vector3 targetPos, System.Action<Vector3, bool> callback)
    {
        // Use Vuforia's PerformHitTest to project position onto detected ground plane
        if (planeFinder != null)
        {
            // Convert world position to screen point for hit test
            Vector3 screenPoint = Camera.main.WorldToScreenPoint(targetPos);
            Vector2 screenPos = new Vector2(screenPoint.x, screenPoint.y);
            
            // Perform hit test using Vuforia PlaneFinderBehaviour
            bool hitResult = planeFinder.PerformHitTest(screenPos);
            
            if (hitResult)
            {
                // Get the projected position from the ground plane stage
                Vector3 projectedPosition = groundPlaneStage.transform.position;
                projectedPosition.x = targetPos.x;
                projectedPosition.z = targetPos.z;
                
                callback?.Invoke(projectedPosition, true);
                yield break;
            }
        }
        
        // Fallback if hit test fails
        callback?.Invoke(targetPos, false);
    }
    
    void LoadChunkAtPosition(Vector2 chunkCoord, Vector3 position)
    {
        if (loadedChunks.ContainsKey(chunkCoord))
            return;
        
        GameObject chunkObj = new GameObject($"VuforiaChunk_{chunkCoord.x}_{chunkCoord.y}");
        chunkObj.transform.position = position;
        
        // Make chunk a child of Ground Plane Stage for proper tracking
        if (groundPlaneStage != null)
        {
            chunkObj.transform.SetParent(groundPlaneStage.transform);
        }
        
        VuforiaChunkData chunkData = chunkObj.AddComponent<VuforiaChunkData>();
        chunkData.Initialize(chunkCoord, currentVersion);
        
        // Load model from Redis/S3 system
        StartCoroutine(LoadModelForChunk(chunkData));
        
        loadedChunks[chunkCoord] = chunkData;
        Debug.Log($"[Vuforia] Loaded chunk {chunkCoord} at ground plane position {position}");
    }
    
    void PlaceChunkAtPosition(Vector3 position)
    {
        Vector2 chunkCoord = WorldToChunkCoord(position);
        
        if (!loadedChunks.ContainsKey(chunkCoord))
        {
            LoadChunkAtPosition(chunkCoord, position);
        }
    }
    
    // ============== MODEL LOADING FROM REDIS/S3 ==============
    IEnumerator LoadModelForChunk(VuforiaChunkData chunkData)
    {
        string modelKey = $"chunk_model_{chunkData.chunkCoordinate.x}_{chunkData.chunkCoordinate.y}_v{currentVersion}";
        
        // Try to load from Redis cache first, fallback to S3
        yield return redisClient.GetFromRedis(modelKey, (modelData, fromRedis) =>
        {
            if (!string.IsNullOrEmpty(modelData))
            {
                StartCoroutine(InstantiateModelFromData(chunkData, modelData, fromRedis));
            }
            else
            {
                // Use fallback prefab if no cached model data
                InstantiateFallbackModel(chunkData);
            }
        });
    }
    
    IEnumerator InstantiateModelFromData(VuforiaChunkData chunkData, string modelData, bool fromCache)
    {
        Debug.Log($"[Model Loading] Loading model for chunk {chunkData.chunkCoordinate} from {(fromCache ? "Redis" : "S3")}");
        
        // Here you would parse modelData and instantiate the actual 3D model
        // For now, using a prefab as example
        if (modelPrefabs.Length > 0)
        {
            int modelIndex = Mathf.Abs((int)(chunkData.chunkCoordinate.x + chunkData.chunkCoordinate.y)) % modelPrefabs.Length;
            GameObject modelPrefab = modelPrefabs[modelIndex];
            
            if (modelPrefab != null)
            {
                GameObject model = Instantiate(modelPrefab, chunkData.transform);
                model.transform.localPosition = Vector3.zero;
                model.transform.localScale = Vector3.one * 0.1f; // Scale for AR
                
                chunkData.SetModel(model);
                Debug.Log($"[Vuforia] Model instantiated for chunk {chunkData.chunkCoordinate}");
            }
        }
        
        yield return null;
    }
    
    void InstantiateFallbackModel(VuforiaChunkData chunkData)
    {
        // Create a simple cube as fallback
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.transform.SetParent(chunkData.transform);
        cube.transform.localPosition = Vector3.zero;
        cube.transform.localScale = Vector3.one * 0.5f;
        
        chunkData.SetModel(cube);
        Debug.Log($"[Vuforia] Fallback model created for chunk {chunkData.chunkCoordinate}");
    }
    
    // ============== MOVEMENT DETECTION (Same as before) ==============
    IEnumerator DetectPlayerMovement()
    {
        while (true)
        {
            yield return new WaitForSeconds(movementCheckInterval);
            
            currentPlayerPosition = Camera.main.transform.position;
            float distanceMoved = Vector3.Distance(currentPlayerPosition, lastPlayerPosition);
            
            if (distanceMoved > movementThreshold)
            {
                if (!playerIsMoving)
                {
                    OnPlayerStartedMoving();
                }
                playerIsMoving = true;
                OnPlayerMoving(distanceMoved);
            }
            else
            {
                if (playerIsMoving)
                {
                    OnPlayerStoppedMoving();
                }
                playerIsMoving = false;
            }
            
            lastPlayerPosition = currentPlayerPosition;
        }
    }
    
    void OnPlayerStartedMoving()
    {
        Debug.Log("[Vuforia] Player started moving - initiating chunk preloading");
        if (groundPlaneInitialized)
        {
            UpdateChunkSystem();
        }
    }
    
    void OnPlayerMoving(float distance)
    {
        if (groundPlaneInitialized)
        {
            CheckVersionRequirements();
            UpdateChunkSystem();
        }
    }
    
    void OnPlayerStoppedMoving()
    {
        Debug.Log("[Vuforia] Player stopped moving - optimizing loaded chunks");
        OptimizeLoadedChunks();
    }
    
    void UpdateChunkSystem()
    {
        Vector2 playerChunk = WorldToChunkCoord(currentPlayerPosition);
        
        // Find chunks that should be loaded around player
        List<Vector2> requiredChunks = GetChunksInRadius(playerChunk, chunkRadius);
        
        foreach (Vector2 chunk in requiredChunks)
        {
            if (!loadedChunks.ContainsKey(chunk) && !chunksToLoad.Contains(chunk))
            {
                chunksToLoad.Enqueue(chunk);
            }
        }
    }

    // ============== MISSING FUNCTION PLACEHOLDERS ==============
    IEnumerator ProcessChunkLoading()
    {
        // This is a placeholder for the missing function
        yield return null;
    }

    IEnumerator PreloadNextVersion()
    {
        // This is a placeholder for the missing function
        yield return null;
    }

    void CheckVersionRequirements()
    {
        // This is a placeholder for the missing function
    }

    void OptimizeLoadedChunks()
    {
        // This is a placeholder for the missing function
    }

    List<Vector2> GetChunksInRadius(Vector2 center, float radius)
    {
        // This is a placeholder for the missing function
        return new List<Vector2>();
    }
    
    // ============== UTILITY FUNCTIONS (Adapted for Vuforia) ==============
    Vector2 WorldToChunkCoord(Vector3 worldPos)
    {
        return new Vector2(
            Mathf.Floor(worldPos.x / chunkRadius),
            Mathf.Floor(worldPos.z / chunkRadius)
        );
    }
    
    Vector3 ChunkToWorldCoord(Vector2 chunkCoord)
    {
        return new Vector3(
            chunkCoord.x * chunkRadius + chunkRadius * 0.5f,
            0f, // Y will be projected to ground plane
            chunkCoord.y * chunkRadius + chunkRadius * 0.5f
        );
    }
    
    // ============== DEBUG INFO ==============
    #if UNITY_EDITOR
    void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 10, 300, 220));
        
        GUILayout.Label($"Vuforia AR Version Preloader");
        GUILayout.Label($"Current Version: {currentVersion}");
        GUILayout.Label($"Ground Plane: {(groundPlaneInitialized ? "Initialized" : "Waiting...")}");
        GUILayout.Label($"Player Moving: {playerIsMoving}");
        GUILayout.Label($"Loaded Chunks: {loadedChunks.Count}");
        GUILayout.Label($"Cached Chunks: {cachedChunks.Count}");
        
        Vector2 playerChunk = WorldToChunkCoord(currentPlayerPosition);
        GUILayout.Label($"Player Chunk: ({playerChunk.x}, {playerChunk.y})");
        
        GUILayout.EndArea();
    }
    #endif
}

// ============== VUFORIA CHUNK DATA CLASS ==============
public class VuforiaChunkData : MonoBehaviour
{
    public Vector2 chunkCoordinate;
    public string chunkVersion;
    public bool isLoaded = false;
    public bool isPriority = false;
    public float loadTime;
    public GameObject loadedModel;
    
    public void Initialize(Vector2 coord, string version)
    {
        chunkCoordinate = coord;
        chunkVersion = version;
        isLoaded = true;
        loadTime = Time.time;
    }
    
    public void SetModel(GameObject model)
    {
        loadedModel = model;
    }
    
    public void SetPriority(bool priority)
    {
        isPriority = priority;
    }
}
