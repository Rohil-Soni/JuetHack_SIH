# JuetHack_SIH
Unity AR Model Loader with AWS S3 & Redis
[![Unity](https://img.shields.io/badge/Unity-2022.3.62f1-black?ps://img.shields.io![Android](https://img.shields.io/badge/Platform-Android-green?logo=t dynamically loads 3D models from AWS S3 using Redis caching and Vuforia ground plane detection. Built for SIH 2025 hackathon project.

🚀 Features
Core Functionality
AR Ground Plane Detection - Vuforia-powered surface tracking

Dynamic Model Loading - Runtime GLB/GLTF loading from AWS S3

Redis Integration - Fast metadata caching and retrieval

Smart Fallback System - Colorful cubes when API fails

Build-Safe Development - Toggle API calls for editor vs builds

Advanced Features
AR Stability System - Drift correction and anchor stabilization

Interactive Placement - Tap-to-place models with ground detection

Local Anchor Caching - Persistent AR positions between sessions

Physics Integration - Automatic rigidbody and collider generation

Performance Monitoring - Real-time FPS and network status

🛠 Installation & Setup
Prerequisites
Unity 2022.3.62f1 or newer

Android Build Support module

Valid Vuforia license key

Required Packages
Install via Package Manager:

text
com.unity.cloud.gltfast (6.1.0+)
com.ptc.vuforia.engine (10.x)  
com.google.ar.core.arfoundation.extensions (6.1.0+)
Unity Project Setup
Clone repository and open in Unity

Window → Vuforia Configuration → Add license key

Enable Ground Plane feature in Vuforia settings

Import packages and let Unity compile

Android Build Configuration
text
File → Build Settings → Android:
✅ Scripting Backend: IL2CPP
✅ Target Architecture: ARM64 only
✅ Minimum API Level: Android 10.0 (API 29)

Edit → Project Settings → Player → Android:
✅ Internet Access: Require
✅ Package Name: com.yourname.arapp
✅ Active Input Handling: Input Manager (Old)
⚙️ Configuration
Script Configuration
Attach VuforiaAWSRedisLoader.cs to a GameObject and configure:

csharp
[Header("=== BUILD MODE CONTROL ===")]
public bool enableAPICallsInEditor = false; // UNCHECK for building

[Header("=== AWS REDIS CONFIGURATION ===")]  
public string apiBaseUrl = "http://43.205.215.100:5000";
public string[] availableModelKeys = {"model:temple1", "model:temple2", "model:building1"};

[Header("=== MODEL CONFIGURATION ===")]
public float modelScale = 0.1f;
public GameObject[] fallbackPrefabs; // Optional custom fallbacks
API Response Format
Your Redis API should return:

json
{
  "success": true,
  "key": "model:temple1", 
  "value": {
    "s3_path": "https://your-bucket.s3.amazonaws.com/temple1.glb",
    "scale": "0.1",
    "rotation": "0,0,0"
  }
}
🎮 Usage
Getting Started
Build and install APK on ARCore-compatible Android device

Grant camera permissions when prompted

Point camera at ground until plane is detected

Tap screen to place 3D models at detected positions

Debug Features (Editor Only)
Toggle API Calls - Test with/without network

Load Specific Models - Test individual model keys

Monitor Performance - View FPS and network status

Cache Management - Save/clear anchor positions

🏗 Architecture
Key Components
VuforiaAWSRedisLoader.cs - Main AR controller script

Ground Plane Detection - Vuforia plane finding and tracking

Model Loading Pipeline - Coroutine-based GLB loading

Stability System - Drift correction and smooth positioning

Local Caching - Anchor persistence and session management

Build Safety
Development Mode - API calls disabled during builds

Production Mode - Full functionality in final APK

Automatic Fallbacks - Colored cubes when models fail to load

Network Detection - Offline/online handling

🔧 Troubleshooting
Common Build Issues
Error	Solution
"Cannot build player while importing"	Delete Library/ and Temp/ folders
"Vulkan not supported"	Switch to Built-in Render Pipeline
"Package Name error"	Set valid package name: com.company.app
"Input Handling warning"	Set to "Input Manager (Old)"
Runtime Issues
Models not loading: Check internet connection and API server status

Ground plane not detected: Point camera at textured flat surface

App crashes: Ensure minimum API level 29 and ARM64 architecture

Poor performance: Reduce model complexity or disable physics

📱 Deployment
Supported Devices
Android 10.0+ (API Level 29+)

ARCore compatible devices (Check compatibility)

Minimum 3GB RAM recommended

Build Process
Set enableAPICallsInEditor = false

File → Build Settings → Build

Install APK on device

Test ground plane detection and model loading

🤝 Contributing
Feel free to fork, modify, and submit pull requests! This project was built for educational purposes.

Tech Stack
Unity 2022.3.62f1 - Game engine and AR framework

Vuforia Engine - AR tracking and ground plane detection

GLTFast - Runtime 3D model loading

AWS S3 - Cloud model storage

Redis - Fast metadata caching

📄 License
MIT License - feel free to use for your own projects!

🙏 Acknowledgments
SIH 2025 - Smart India Hackathon project

JUET - Academic institution support

Unity Technologies - AR development platform

Vuforia - AR tracking solutions

