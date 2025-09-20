using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Vuforia;

// ============== MAIN VERSION PRELOADER SCRIPT ==============
public class ARVersionPreloader : MonoBehaviour
{
    [Header("Version Management")]
    public string currentVersion = "1.0.0";
    public bool autoPreloadNextVersion = true;
    
    [Header("Player Movement Detection")]
    public float movementThreshold = 0.5f;
    public float movementCheckInterval = 0.1f;
    
    [Header("Chunk System")]
    public float chunkRadius = 6f; // 12 chunks radius as noted
    public int maxChunksLoaded = 150; // Optimized for performance
    public int priorityChunkRadius = 3; // Always keep these loaded
    public int preloadChunksAhead = 8;
    
    [Header("Performance")]
    public int maxChunksPerFrame = 2;
    public float chunkUnloadDelay = 5f;
    
    private Vector3 lastPlayerPosition;
    private Vector3 currentPlayerPosition;
    private bool playerIsMoving = false;
    
    private Dictionary<Vector2, ChunkData> loadedChunks = new Dictionary<Vector2, ChunkData>();
    private Dictionary<Vector2, ChunkData> cachedChunks = new Dictionary<Vector2, ChunkData>();
    private Queue<Vector2> chunksToLoad = new Queue<Vector2>();
    private Queue<Vector2> chunksToUnload = new Queue<Vector2>();
    
    void Start()
    {
        InitializeVersionPreloader();
        StartCoroutine(DetectPlayerMovement());
        StartCoroutine(ProcessChunkLoading());
    }
    
    // ============== VERSION PRELOADING ==============
    void InitializeVersionPreloader()
    {
        Debug.Log($"Initializing AR Version Preloader - Current Version: {currentVersion}");
        
        // Set initial player position
        lastPlayerPosition = transform.position;
        currentPlayerPosition = transform.position;
        
        // Load initial chunks around player
        LoadInitialChunks();
        
        if (autoPreloadNextVersion)
        {
            StartCoroutine(PreloadNextVersion());
        }
    }
    
    IEnumerator PreloadNextVersion()
    {
        yield return new WaitForSeconds(2f); // Wait for initial setup
        
        Debug.Log("Preloading next version in background...");
        
        // Simulate version preloading logic
        string nextVersion = GetNextVersion();
        yield return StartCoroutine(LoadVersionInBackground(nextVersion));
        
        Debug.Log($"Next version {nextVersion} preloaded successfully");
    }
    
    string GetNextVersion()
    {
        // Simple version increment logic
        string[] versionParts = currentVersion.Split('.');
        int patchVersion = int.Parse(versionParts[2]) + 1;
        return $"{versionParts[0]}.{versionParts[1]}.{patchVersion}";
    }
    
    IEnumerator LoadVersionInBackground(string version)
    {
        float loadProgress = 0f;
        
        while (loadProgress < 1f)
        {
            loadProgress += Time.deltaTime * 0.1f; // Simulate loading
            yield return null;
        }
        
        Debug.Log($"Version {version} loaded in background");
    }
    
    // ============== PLAYER MOVEMENT DETECTION ==============
    IEnumerator DetectPlayerMovement()
    {
        while (true)
        {
            yield return new WaitForSeconds(movementCheckInterval);
            
            currentPlayerPosition = transform.position;
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
        Debug.Log("Player started moving - initiating chunk preloading");
        PredictMovementDirection();
    }
    
    void OnPlayerMoving(float distance)
    {
        // Check which version should be loaded based on movement
        CheckVersionRequirements();
        
        // Update chunk system based on new position
        UpdateChunkSystem();
    }
    
    void OnPlayerStoppedMoving()
    {
        Debug.Log("Player stopped moving - optimizing loaded chunks");
        OptimizeLoadedChunks();
    }
    
    void PredictMovementDirection()
    {
        Vector3 movementDirection = (currentPlayerPosition - lastPlayerPosition).normalized;
        PreloadChunksInDirection(movementDirection);
    }
    
    // ============== CHUNK SYSTEM ==============
    void LoadInitialChunks()
    {
        Vector2 playerChunk = WorldToChunkCoord(currentPlayerPosition);
        
        for (int x = -2; x <= 2; x++)
        {
            for (int z = -2; z <= 2; z++)
            {
                Vector2 chunkCoord = new Vector2(playerChunk.x + x, playerChunk.y + z);
                LoadChunk(chunkCoord, true); // Force load initial chunks
            }
        }
    }
    
    void UpdateChunkSystem()
    {
        Vector2 playerChunk = WorldToChunkCoord(currentPlayerPosition);
        
        // Find chunks that should be loaded
        List<Vector2> requiredChunks = GetChunksInRadius(playerChunk, chunkRadius);
        
        // Queue chunks for loading
        foreach (Vector2 chunk in requiredChunks)
        {
            if (!loadedChunks.ContainsKey(chunk) && !chunksToLoad.Contains(chunk))
            {
                chunksToLoad.Enqueue(chunk);
            }
        }
        
        // Queue distant chunks for unloading
        List<Vector2> chunksToRemove = new List<Vector2>();
        foreach (var chunk in loadedChunks.Keys)
        {
            if (Vector2.Distance(chunk, playerChunk) > chunkRadius + 5f)
            {
                chunksToRemove.Add(chunk);
            }
        }
        
        foreach (var chunk in chunksToRemove)
        {
            if (!chunksToUnload.Contains(chunk))
            {
                chunksToUnload.Enqueue(chunk);
            }
        }
    }
    
    IEnumerator ProcessChunkLoading()
    {
        while (true)
        {
            int chunksProcessedThisFrame = 0;
            
            // Load chunks
            while (chunksToLoad.Count > 0 && chunksProcessedThisFrame < maxChunksPerFrame)
            {
                Vector2 chunkToLoad = chunksToLoad.Dequeue();
                LoadChunk(chunkToLoad);
                chunksProcessedThisFrame++;
                yield return null; // Spread across frames
            }
            
            // Unload chunks
            while (chunksToUnload.Count > 0 && chunksProcessedThisFrame < maxChunksPerFrame)
            {
                Vector2 chunkToUnload = chunksToUnload.Dequeue();
                UnloadChunk(chunkToUnload);
                chunksProcessedThisFrame++;
                yield return null; // Spread across frames
            }
            
            yield return null; // Wait one frame
        }
    }
    
    void LoadChunk(Vector2 chunkCoord, bool forceLoad = false)
    {
        if (loadedChunks.ContainsKey(chunkCoord))
            return;
        
        Vector2 playerChunk = WorldToChunkCoord(currentPlayerPosition);
        float distanceFromPlayer = Vector2.Distance(chunkCoord, playerChunk);
        
        // Always load priority chunks (close to player)
        bool isPriorityChunk = distanceFromPlayer <= priorityChunkRadius;
        
        if (!forceLoad && !isPriorityChunk && loadedChunks.Count >= maxChunksLoaded)
        {
            // Try to unload a distant chunk first
            UnloadDistantChunk();
            
            if (loadedChunks.Count >= maxChunksLoaded)
            {
                Debug.Log($"Max chunks loaded ({maxChunksLoaded}), cannot load chunk {chunkCoord}");
                return;
            }
        }
        
        ChunkData chunkData;
        
        // Check if chunk is cached first
        if (cachedChunks.ContainsKey(chunkCoord))
        {
            chunkData = cachedChunks[chunkCoord];
            cachedChunks.Remove(chunkCoord);
            Debug.Log($"Loading chunk {chunkCoord} from cache (Distance: {distanceFromPlayer:F1})");
        }
        else
        {
            // Create new chunk
            chunkData = CreateNewChunk(chunkCoord);
            Debug.Log($"Creating new chunk {chunkCoord} (Distance: {distanceFromPlayer:F1})");
        }
        
        loadedChunks[chunkCoord] = chunkData;
        chunkData.gameObject.SetActive(true);
        chunkData.SetPriority(isPriorityChunk);
    }
    
    void UnloadChunk(Vector2 chunkCoord)
    {
        if (!loadedChunks.ContainsKey(chunkCoord))
            return;
        
        ChunkData chunkData = loadedChunks[chunkCoord];
        loadedChunks.Remove(chunkCoord);
        
        // Check if we should cache this chunk or destroy it
        if (ShouldCacheChunk(chunkCoord))
        {
            cachedChunks[chunkCoord] = chunkData;
            chunkData.gameObject.SetActive(false);
            Debug.Log($"Caching chunk {chunkCoord}");
        }
        else
        {
            DestroyChunk(chunkData);
            Debug.Log($"Destroying chunk {chunkCoord}");
        }
    }
    
    ChunkData CreateNewChunk(Vector2 chunkCoord)
    {
        Vector3 worldPos = ChunkToWorldCoord(chunkCoord);
        GameObject chunkObj = new GameObject($"Chunk_{chunkCoord.x}_{chunkCoord.y}");
        chunkObj.transform.position = worldPos;
        
        ChunkData chunkData = chunkObj.AddComponent<ChunkData>();
        chunkData.Initialize(chunkCoord);
        
        // Add some visual representation for testing
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.transform.parent = chunkObj.transform;
        cube.transform.localPosition = Vector3.zero;
        cube.transform.localScale = Vector3.one * 10f; // Make it visible
        
        return chunkData;
    }
    
    void DestroyChunk(ChunkData chunkData)
    {
        if (chunkData != null && chunkData.gameObject != null)
        {
            Destroy(chunkData.gameObject);
        }
    }
    
    bool ShouldCacheChunk(Vector2 chunkCoord)
    {
        Vector2 playerChunk = WorldToChunkCoord(currentPlayerPosition);
        float distance = Vector2.Distance(chunkCoord, playerChunk);
        
        // Cache chunks that are close but not immediately needed
        return distance < chunkRadius + 10f && cachedChunks.Count < 20;
    }
    
    void PreloadChunksInDirection(Vector3 direction)
    {
        Vector2 playerChunk = WorldToChunkCoord(currentPlayerPosition);
        Vector2 directionChunk = new Vector2(direction.x, direction.z).normalized;
        
        // Preload chunks ahead of movement direction
        for (int i = 1; i <= preloadChunksAhead; i++)
        {
            Vector2 targetChunk = playerChunk + (directionChunk * i);
            Vector2 roundedChunk = new Vector2(Mathf.Round(targetChunk.x), Mathf.Round(targetChunk.y));
            
            if (!loadedChunks.ContainsKey(roundedChunk) && !chunksToLoad.Contains(roundedChunk))
            {
                chunksToLoad.Enqueue(roundedChunk);
            }
        }
    }
    
    void UnloadDistantChunk()
    {
        Vector2 playerChunk = WorldToChunkCoord(currentPlayerPosition);
        Vector2 furthestChunk = Vector2.zero;
        float furthestDistance = 0f;
        
        // Find the furthest non-priority chunk
        foreach (var kvp in loadedChunks)
        {
            Vector2 chunkCoord = kvp.Key;
            ChunkData chunkData = kvp.Value;
            
            if (!chunkData.isPriority) // Don't unload priority chunks
            {
                float distance = Vector2.Distance(chunkCoord, playerChunk);
                if (distance > furthestDistance)
                {
                    furthestDistance = distance;
                    furthestChunk = chunkCoord;
                }
            }
        }
        
        if (furthestDistance > 0)
        {
            UnloadChunk(furthestChunk);
            Debug.Log($"Auto-unloaded distant chunk {furthestChunk} (Distance: {furthestDistance:F1})");
        }
    }
    
    // ============== VERSION CHECKING ==============
    void CheckVersionRequirements()
    {
        Vector2 playerChunk = WorldToChunkCoord(currentPlayerPosition);
        
        // Check if we need to load a different version based on location
        string requiredVersion = GetRequiredVersionForChunk(playerChunk);
        
        if (requiredVersion != currentVersion)
        {
            StartCoroutine(SwitchToVersion(requiredVersion));
        }
    }
    
    string GetRequiredVersionForChunk(Vector2 chunkCoord)
    {
        // Example logic - different areas might need different versions
        if (chunkCoord.x > 10 || chunkCoord.y > 10)
        {
            return "1.1.0"; // Newer version for distant areas
        }
        return currentVersion;
    }
    
    IEnumerator SwitchToVersion(string targetVersion)
    {
        Debug.Log($"Switching from {currentVersion} to {targetVersion}");
        
        // Simulate version switching
        yield return new WaitForSeconds(1f);
        
        currentVersion = targetVersion;
        Debug.Log($"Successfully switched to version {targetVersion}");
    }
    
    // ============== UTILITY FUNCTIONS ==============
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
            0f,
            chunkCoord.y * chunkRadius + chunkRadius * 0.5f
        );
    }
    
    List<Vector2> GetChunksInRadius(Vector2 centerChunk, float radius)
    {
        List<Vector2> chunks = new List<Vector2>();
        int chunkRadius = Mathf.CeilToInt(radius / this.chunkRadius);
        
        for (int x = -chunkRadius; x <= chunkRadius; x++)
        {
            for (int z = -chunkRadius; z <= chunkRadius; z++)
            {
                Vector2 chunkCoord = centerChunk + new Vector2(x, z);
                if (Vector2.Distance(chunkCoord, centerChunk) <= chunkRadius)
                {
                    chunks.Add(chunkCoord);
                }
            }
        }
        
        return chunks;
    }
    
    // ============== DEBUG INFO ==============
    void OnGUI()
    {
        if (Application.isEditor)
        {
            GUILayout.BeginArea(new Rect(10, 10, 300, 200));
            
            GUILayout.Label($"Current Version: {currentVersion}");
            GUILayout.Label($"Player Moving: {playerIsMoving}");
            GUILayout.Label($"Loaded Chunks: {loadedChunks.Count}");
            GUILayout.Label($"Cached Chunks: {cachedChunks.Count}");
            GUILayout.Label($"Chunks to Load: {chunksToLoad.Count}");
            GUILayout.Label($"Chunks to Unload: {chunksToUnload.Count}");
            
            Vector2 playerChunk = WorldToChunkCoord(currentPlayerPosition);
            GUILayout.Label($"Player Chunk: ({playerChunk.x}, {playerChunk.y})");
            
            GUILayout.EndArea();
        }
    }
}

// ============== CHUNK DATA CLASS ==============
public class ChunkData : MonoBehaviour
{
    public Vector2 chunkCoordinate;
    public string chunkVersion;
    public bool isLoaded = false;
    public bool isPriority = false; // Priority chunks stay loaded
    public float loadTime;
    
    public void Initialize(Vector2 coord)
    {
        chunkCoordinate = coord;
        chunkVersion = "1.0.0"; // Default version
        isLoaded = true;
        loadTime = Time.time;
    }
    
    public void SetPriority(bool priority)
    {
        isPriority = priority;
    }
    
    public void SetVersion(string version)
    {
        chunkVersion = version;
    }
    
    public bool IsVersionCompatible(string requiredVersion)
    {
        return chunkVersion == requiredVersion;
    }
    
    void OnDestroy()
    {
        Debug.Log($"Chunk {chunkCoordinate} destroyed (Priority: {isPriority})");
    }
}