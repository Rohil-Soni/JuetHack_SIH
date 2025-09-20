using UnityEngine;
using UnityEngine.Networking;
using System.Collections;

/// <summary>
/// HTTP-based Redis client for Unity with S3 fallback
/// Handles API calls to your Flask server on EC2
/// </summary>
[System.Serializable]
public class RedisHttpClient
{
    [Header("=== REDIS API CONFIGURATION ===")]
    public string redisApiBaseUrl = "http://43.205.215.100:5000";
    public string authToken = ""; // Leave empty if no auth needed
    public int requestTimeoutSeconds = 15;
    
    [Header("=== S3 FALLBACK CONFIGURATION ===")]
    public string s3ApiBaseUrl = ""; // Can be empty for now
    public string s3BucketName = ""; // Can be empty for now

    // HTTP request methods
    public IEnumerator GetFromRedis(string key, System.Action<string, bool> callback)
    {
        string url = $"{redisApiBaseUrl}/get/{key}";
        
        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            // Set headers if auth token is provided
            if (!string.IsNullOrEmpty(authToken))
            {
                request.SetRequestHeader("Authorization", $"Bearer {authToken}");
            }
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = requestTimeoutSeconds;

            Debug.Log($"[Redis] Requesting: {url}");
            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success && !string.IsNullOrEmpty(request.downloadHandler.text))
            {
                Debug.Log($"[Redis] Success response: {request.downloadHandler.text}");
                callback?.Invoke(request.downloadHandler.text, true);
            }
            else
            {
                Debug.LogWarning($"[Redis] Failed for key: {key}, Error: {request.error}");
                
                // Try S3 fallback if configured
                if (!string.IsNullOrEmpty(s3ApiBaseUrl))
                {
                    yield return GetFromS3(key, callback);
                }
                else
                {
                    callback?.Invoke(null, false);
                }
            }
        }
    }

    public IEnumerator SetInRedis(string key, string value, int ttlSeconds = 3600, System.Action<bool> callback = null)
    {
        string url = $"{redisApiBaseUrl}/set";
        
        var requestData = new RedisSetRequest
        {
            key = key,
            value = value,
            ttl = ttlSeconds
        };

        string jsonData = JsonUtility.ToJson(requestData);

        using (UnityWebRequest request = UnityWebRequest.Post(url, jsonData, "application/json"))
        {
            if (!string.IsNullOrEmpty(authToken))
            {
                request.SetRequestHeader("Authorization", $"Bearer {authToken}");
            }
            request.timeout = requestTimeoutSeconds;

            Debug.Log($"[Redis] Setting key: {key}");
            yield return request.SendWebRequest();

            bool success = request.result == UnityWebRequest.Result.Success;
            if (success)
            {
                Debug.Log($"[Redis] Successfully set key: {key}");
            }
            else
            {
                Debug.LogWarning($"[Redis] Failed to set key: {key}, Error: {request.error}");
            }

            callback?.Invoke(success);
        }
    }

    private IEnumerator GetFromS3(string key, System.Action<string, bool> callback)
    {
        if (string.IsNullOrEmpty(s3ApiBaseUrl))
        {
            Debug.LogWarning("[S3] S3 fallback not configured");
            callback?.Invoke(null, false);
            yield break;
        }

        string url = $"{s3ApiBaseUrl}/objects/{s3BucketName}/{key}";
        
        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            if (!string.IsNullOrEmpty(authToken))
            {
                request.SetRequestHeader("Authorization", $"Bearer {authToken}");
            }
            request.timeout = requestTimeoutSeconds;

            Debug.Log($"[S3] Fallback request: {url}");
            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                string s3Data = request.downloadHandler.text;
                Debug.Log($"[S3] Successfully retrieved fallback data for key: {key}");
                
                // Cache the S3 data back in Redis for future requests
                yield return SetInRedis(key, s3Data, 1800); // 30 minutes TTL
                
                callback?.Invoke(s3Data, false);
            }
            else
            {
                Debug.LogError($"[S3] Fallback failed for key: {key}, Error: {request.error}");
                callback?.Invoke(null, false);
            }
        }
    }

    // Test connection method
    public IEnumerator TestConnection(System.Action<bool> callback)
    {
        string url = $"{redisApiBaseUrl}/health"; // Assuming you have a health check endpoint
        
        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            request.timeout = 5; // Short timeout for health check
            yield return request.SendWebRequest();
            
            bool isConnected = request.result == UnityWebRequest.Result.Success;
            Debug.Log($"[Redis] Connection test: {(isConnected ? "SUCCESS" : "FAILED")}");
            callback?.Invoke(isConnected);
        }
    }
}

// Supporting data classes
[System.Serializable]
public class RedisResponse
{
    public bool success;
    public string data;
    public string message;
    public string error;
}

[System.Serializable]
public class RedisSetRequest
{
    public string key;
    public string value;
    public int ttl;
}

[System.Serializable]
public class RedisMetadataResponse
{
    public bool success;
    public string key;
    public RedisMetadata value;
}

[System.Serializable]
public class RedisMetadata
{
    public string s3_path;
    public string version;
    public string scale;
    public string rotation;
    public string description;
}
