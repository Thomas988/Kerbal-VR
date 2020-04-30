using System;
using System.Collections;
using System.Runtime.InteropServices;
using UnityEngine;
using Valve.VR;

namespace KerbalVR 
{
    /// <summary>
    /// The entry point for the KerbalVR plugin. This mod should
    /// start up once, when the game starts.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class Core : MonoBehaviour {
        // this function allows importing DLLs from a given path
        [DllImport("kernel32.dll", SetLastError = true)]
        protected static extern bool SetDllDirectory(string lpPathName);

        // import KerbalVR_Renderer plugin functions
        [DllImport("KerbalVR_Renderer")]
        private static extern IntPtr GetRenderEventFunc();

        [DllImport("KerbalVR_Renderer")]
        private static extern void SetTextureFromUnity(
            int textureIndex,
            System.IntPtr textureHandle,
            float boundsUMin,
            float boundsUMax,
            float boundsVMin,
            float boundsVMax);


        #region Types
        public enum OpenVrState {
            Uninitialized,
            Initializing,
            Initialized,
            InitFailed,
        }
        #endregion


        #region Properties
        /// <summary>
        /// Returns true if VR is currently enabled. Enabled
        /// does not necessarily mean VR is currently running, only
        /// that the user allowing VR to be activated.
        /// Set to true to enable VR; false to disable VR.
        /// </summary>
        public static bool VrIsEnabled { get; set; } = false;

        /// <summary>
        /// Returns true if VR is allowed to run in the current scene.
        /// </summary>
        public static bool VrIsAllowed { get; private set; } = true;

        /// <summary>
        /// Returns true if VR is currently running, i.e. tracking devices
        /// and rendering images to the headset.
        /// </summary>
        public static bool VrIsRunning { get; private set; } = false;

        // these arrays each hold one object for the corresponding eye, where
        // index 0 = Left_Eye, index 1 = Right_Eye
        public static RenderTexture[] HmdEyeRenderTexture { get; private set; } = new RenderTexture[2];

        #endregion


        #region Private Members
        // keep track of when the HMD is rendering images
        protected static OpenVrState openVrState = OpenVrState.Uninitialized;
        protected static bool vrIsRunningPrev = false;
        protected static DateTime openVrInitLastAttempt;

        // store the tracked device poses
        protected static TrackedDevicePose_t[] devicePoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        protected static TrackedDevicePose_t[] renderPoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        protected static TrackedDevicePose_t[] gamePoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        #endregion


        //
        // DEBUG HOOKS ---------------------------------------------------------
        //
        private class TimingData {
            public bool isCollecting;
            public double[] data;
            public int index;
        }
        private TimingData[] samples = new TimingData[2];
        private const int NUM_SAMPLES = 1000;

        private void CollectTimeSamples(int type) {
            if (samples[type].isCollecting) {
                if (samples[type].index < NUM_SAMPLES) {
                    samples[type].data[samples[type].index] =
                        (double)DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
                    samples[type].index += 1;
                }
                else {
                    samples[type].isCollecting = false;
                    samples[type].index = 0;

                    string logMsg = "DATA COLLECTION " + type + "\n";
                    for (int i = 0; i < NUM_SAMPLES; ++i) {
                        logMsg += samples[type].data[i].ToString("F6") + "\n";
                    }
                    Debug.Log(logMsg);
                }
            }
        }
        //
        // ---------------------------------------------------------------------

        /// <summary>
        /// Initialize the KerbalVR-related GameObjects, singleton classes, and initialize OpenVR.
        /// </summary>
        protected void Awake() {
            // set the location of the native plugin DLLs
            SetDllDirectory(Globals.EXTERNAL_DLL_PATH);

            //
            // DEBUG HOOKS -----------------------------------------------------
            //
            samples[0] = new TimingData();
            samples[0].isCollecting = false;
            samples[0].data = new double[NUM_SAMPLES];
            samples[0].index = 0;

            samples[1] = new TimingData();
            samples[1].isCollecting = false;
            samples[1].data = new double[NUM_SAMPLES];
            samples[1].index = 0;
            //
            // -----------------------------------------------------------------

            // initialize KerbalVR GameObjects
            GameObject kvrConfiguration = new GameObject("KVR_Configuration");
            kvrConfiguration.AddComponent<KerbalVR.Configuration>();
            Configuration kvrConfigurationComponent = Configuration.Instance; // init the singleton
            DontDestroyOnLoad(kvrConfiguration);

            GameObject kvrAssetLoader = new GameObject("KVR_AssetLoader");
            kvrAssetLoader.AddComponent<KerbalVR.AssetLoader>();
            AssetLoader kvrAssetLoaderComponent = AssetLoader.Instance; // init the singleton
            DontDestroyOnLoad(kvrAssetLoader);

            GameObject kvrScene = new GameObject("KVR_Scene");
            kvrScene.AddComponent<KerbalVR.Scene>();
            Scene kvrSceneComponent = Scene.Instance; // init the singleton
            DontDestroyOnLoad(kvrScene);

            // initialize OpenVR immediately if allowed in config
            if (KerbalVR.Configuration.Instance.InitOpenVrAtStartup) {
                TryInitializeOpenVr();
            }

            // add an event triggered when game scene changes
            GameEvents.onGameSceneLoadRequested.Add(OnGameSceneLoadRequested);

            // don't destroy this object when switching scenes
            DontDestroyOnLoad(this);

            // ensure various settings to minimize latency
            Application.targetFrameRate = -1;
            Application.runInBackground = true; // don't require companion window focus
            QualitySettings.maxQueuedFrames = -1;
            QualitySettings.vSyncCount = 0; // this applies to the companion window

            // initialize SteamVR input
            SteamVR_Actions.PreInitialize();
            SteamVR_Input.IdentifyActionsFile();
            SteamVR_Input.Initialize();
            SteamVR_ActionSet actionSet = SteamVR_Input.GetActionSet("default", false, true);
            if (actionSet != null) {
                actionSet.Activate(SteamVR_Input_Sources.Any);
            }
            else {
                Utils.LogError("Action Set 'default' does not exist");
            }
        }

        /// <summary>
        /// Start is called before the first frame update, to queue up the plugin renderer coroutine.
        /// </summary>
        IEnumerator Start() {
            yield return StartCoroutine("CallPluginAtEndOfFrames");
        }

        /// <summary>
        /// The plugin renderer coroutine which must run after all Cameras have rendered,
        /// then issues a callback to the native renderer, so that the native code runs on
        /// Unity's renderer thread.
        /// </summary>
        IEnumerator CallPluginAtEndOfFrames() {
            while (true) {
                // wait until all frame rendering is done
                yield return new WaitForEndOfFrame();

                // timing data collection
                // CollectTimeSamples(0);

                // need to obtain the latest poses for tracked devices. there seems to be two
                // methods, and the latter looks/feels better than the other. I should note
                // here that this function (CallPluginAtEndOfFrames) runs at a faster rate
                // than LateUpdate. getting the pose data in LateUpdate results in unpleasant
                // stuttering of the rendered images in the headset.

                //
                // (1) method: use this predicted photons stuff
                //

                // get latest device poses, emit an event to indicate devices have been updated
                // float secondsToPhotons = Utils.CalculatePredictedSecondsToPhotons();
                // OpenVR.System.GetDeviceToAbsoluteTrackingPose(Scene.Instance.TrackingSpace, 0f, devicePoses);

                //
                // (2) method: get the latest poses
                //
                EVRCompositorError vrCompositorError = OpenVR.Compositor.GetLastPoses(renderPoses, gamePoses);
                if (vrCompositorError != EVRCompositorError.None) {
                    Debug.LogError("GetLastPoses error: " + vrCompositorError.ToString());
                    VrIsEnabled = false;
                }
                SteamVR_Events.NewPoses.Send(gamePoses);

                // if VR is active, issue the render callback
                if (VrIsRunning) {
                    // the "0" currently does nothing on the native plugin code,
                    // so this can actually just be any int.
                    GL.IssuePluginEvent(GetRenderEventFunc(), 0);
                }
            }
        }

        /// <summary>
        /// Overrides the OnDestroy method, called when plugin is destroyed.
        /// </summary>
        protected void OnDestroy() {
            Utils.Log(KerbalVR.Globals.KERBALVR_NAME + " is shutting down...");
            CloseVr();
        }


        /// <summary>
        /// On Update, dispatch OpenVR events, retrieve tracked device poses.
        /// </summary>
        protected void LateUpdate() {
            // debug hooks
            if (Input.GetKeyDown(KeyCode.Y)) {
                // Utils.Log("Debug");
                Utils.PrintAllCameras();
                // debugOn = !debugOn;
                // samples[0].isCollecting = true;
                // samples[1].isCollecting = true;
            }

            // dispatch any OpenVR events
            DispatchOpenVrEvents();

            // process the state of OpenVR
            ProcessOpenVrState();

            // check if we are running the HMD
            VrIsRunning = (openVrState == OpenVrState.Initialized) && VrIsEnabled;

            if (VrIsRunning) {
                // we've just started VR
                if (!vrIsRunningPrev) {
                    Utils.Log("VR is now turned on");
                    ResetInitialHmdPosition();
                }

                // timing data collection
                // CollectTimeSamples(1);

                // copy the rendered image onto the screen (the KSP window)
                Graphics.Blit(HmdEyeRenderTexture[0], null as RenderTexture);

                /**
                 * hmdEyeTransform is in a coordinate system that follows the headset, where
                 * the origin is the headset device position. Therefore the eyes are at a constant
                 * offset from the device. hmdEyeTransform does not change (per eye).
                 *      hmdEyeTransform.x+  towards the right of the headset
                 *      hmdEyeTransform.y+  towards the top the headset
                 *      hmdEyeTransform.z+  towards the front of the headset
                 *
                 * hmdTransform is in a coordinate system set in physical space, where the
                 * origin is the initial seated position. Or for room-scale, the physical origin of the room.
                 *      hmdTransform.x+     towards the right
                 *      hmdTransform.y+     upwards
                 *      hmdTransform.z+     towards the front
                 *
                 *  Scene.InitialPosition and Scene.InitialRotation are the Unity world coordinates where
                 *  we initialize the VR scene, i.e. the origin of a coordinate system that maps
                 *  1-to-1 with physical space.
                 *
                 *  1. Calculate the position of the eye in the physical coordinate system.
                 *  2. Transform the calculated position into Unity world coordinates, offset from
                 *     InitialPosition and InitialRotation.
                 */
            }


            // update controllers input
            SteamVR_Input.Update();

            SteamVR_Action_Boolean action = SteamVR_Input.GetBooleanAction("default", "grabgrip");
            string logMsg = "GrabGrip: ";
            bool state = action.GetState(SteamVR_Input_Sources.LeftHand);
            logMsg += (state ? "LEFT" : "") + " ";
            state = action.GetState(SteamVR_Input_Sources.RightHand);
            logMsg += (state ? "RIGHT" : "") + "\n";

            logMsg += "Squeeze: ";
            SteamVR_Action_Single action2 = SteamVR_Input.GetSingleAction("default", "squeeze");
            float state2 = action2.GetAxis(SteamVR_Input_Sources.LeftHand);
            logMsg += "LEFT " + state2.ToString("F3") + ", ";
            state2 = action2.GetAxis(SteamVR_Input_Sources.RightHand);
            logMsg += "RIGHT " + state2.ToString("F3") + "\n";

            logMsg += "FlightStick: ";
            SteamVR_Action_Vector2 action3 = SteamVR_Input.GetVector2Action("default", "flightstick");
            Vector2 state3 = action3.GetAxis(SteamVR_Input_Sources.LeftHand);
            logMsg += "LEFT " + state3.ToString() + ", ";
            state3 = action3.GetAxis(SteamVR_Input_Sources.RightHand);
            logMsg += "RIGHT " + state3.ToString() + "\n";

            logMsg += "Yaw: ";
            action2 = SteamVR_Input.GetSingleAction("default", "yawcontrol");
            state2 = action2.GetAxis(SteamVR_Input_Sources.LeftHand);
            logMsg += "LEFT " + state2.ToString("F3") + ", ";
            state2 = action2.GetAxis(SteamVR_Input_Sources.RightHand);
            logMsg += "RIGHT " + state2.ToString("F3") + "\n";

            logMsg += "Thrust: ";
            action2 = SteamVR_Input.GetSingleAction("default", "thrustcontrol");
            state2 = action2.GetAxis(SteamVR_Input_Sources.LeftHand);
            logMsg += "LEFT " + state2.ToString("F3") + ", ";
            state2 = action2.GetAxis(SteamVR_Input_Sources.RightHand);
            logMsg += "RIGHT " + state2.ToString("F3") + "\n";


            logMsg += "\n";
            SteamVR_ActionSet[] actionSets = SteamVR_Input.GetActionSets();
            foreach (var a in actionSets) {
                logMsg += a.fullPath + " " + (a.IsActive(SteamVR_Input_Sources.Any) ? "active" : "not active") + "\n";
                foreach (var b in a.allActions) {
                    logMsg += "* " + b.fullPath + " " + (b.active ? "bound" : "unbound") + "\n";
                }
            }
            Utils.SetDebugText(logMsg);



            // VR has been deactivated
            if (!VrIsRunning && vrIsRunningPrev) {
                Utils.Log("VR is now turned off");
            }

            // emit an update if the running status changed
            if (VrIsRunning != vrIsRunningPrev) {
                KerbalVR.Events.HmdStatusUpdated.Send(VrIsRunning);
            }
            vrIsRunningPrev = VrIsRunning;
        }

        /// <summary>
        /// Dispatch other miscellaneous OpenVR-specific events.
        /// </summary>
        protected void DispatchOpenVrEvents() {
            var vrEvent = new VREvent_t();
            var size = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(VREvent_t));
            for (int i = 0; i < 64; i++) {
                if (!OpenVR.System.PollNextEvent(ref vrEvent, size))
                    break;

                switch ((EVREventType)vrEvent.eventType) {
                    case EVREventType.VREvent_InputFocusCaptured: // another app has taken focus (likely dashboard)
                        if (vrEvent.data.process.oldPid == 0) {
                            SteamVR_Events.InputFocus.Send(false);
                        }
                        break;
                    case EVREventType.VREvent_InputFocusReleased: // that app has released input focus
                        if (vrEvent.data.process.pid == 0) {
                            SteamVR_Events.InputFocus.Send(true);
                        }
                        break;
                    case EVREventType.VREvent_ShowRenderModels:
                        SteamVR_Events.HideRenderModels.Send(false);
                        break;
                    case EVREventType.VREvent_HideRenderModels:
                        SteamVR_Events.HideRenderModels.Send(true);
                        break;
                    default:
                        SteamVR_Events.System((EVREventType)vrEvent.eventType).Send(vrEvent);
                        break;
                }
            }
        }

        /// <summary>
        /// State machine which re-attempts to initialize OpenVR periodically if it fails.
        /// </summary>
        protected static void ProcessOpenVrState() {
            switch (openVrState) {
                case OpenVrState.Uninitialized:
                    if (VrIsEnabled) {
                        openVrState = OpenVrState.Initializing;
                    }
                    break;

                case OpenVrState.Initializing:
                    TryInitializeOpenVr();
                    break;

                case OpenVrState.InitFailed:
                    if (DateTime.Now.Subtract(openVrInitLastAttempt).TotalSeconds > 10) {
                        openVrState = OpenVrState.Uninitialized;
                    }
                    break;
            }
        }

        protected static void TryInitializeOpenVr() {
            openVrInitLastAttempt = DateTime.Now;
            try {
                InitializeOpenVr();
                openVrState = OpenVrState.Initialized;
                Utils.Log("Initialized OpenVR");

            } catch (Exception e) {
                openVrState = OpenVrState.InitFailed;
                Utils.LogError("InitializeOpenVr failed with error: " + e);
                VrIsEnabled = false;
            }
        }

        /// <summary>
        /// An event called when the game is switching scenes.
        /// </summary>
        /// <param name="scene">The scene being switched into</param>
        protected void OnGameSceneLoadRequested(GameScenes scene) {
        }

        /// <summary>
        /// Initialize VR using OpenVR API calls. Throws an exception on error.
        /// </summary>
        protected static void InitializeOpenVr() {
            // return if OpenVR has already been initialized
            if (openVrState == OpenVrState.Initialized) {
                return;
            }

            // check if HMD is connected on the system
            if (!OpenVR.IsHmdPresent()) {
                throw new InvalidOperationException("HMD not found on this system");
            }

            // check if SteamVR runtime is installed
            if (!OpenVR.IsRuntimeInstalled()) {
                throw new InvalidOperationException("SteamVR runtime not found on this system");
            }

            // initialize OpenVR
            EVRInitError initErrorCode = EVRInitError.None;
            OpenVR.Init(ref initErrorCode, EVRApplicationType.VRApplication_Scene);
            if (initErrorCode != EVRInitError.None) {
                throw new Exception("OpenVR error: " + OpenVR.GetStringForHmdError(initErrorCode));
            }

            // reset "seated position" and capture initial position. this means you should hold the HMD in
            // the position you would like to consider "seated", before running this code.
            ResetInitialHmdPosition();

            // get HMD render target size
            uint renderTextureWidth = 0;
            uint renderTextureHeight = 0;
            OpenVR.System.GetRecommendedRenderTargetSize(ref renderTextureWidth, ref renderTextureHeight);
            Utils.Log("HMD texture size per eye: " + renderTextureWidth + " x " + renderTextureHeight);

            // check the graphics device
            switch (SystemInfo.graphicsDeviceType) {
                case UnityEngine.Rendering.GraphicsDeviceType.Direct3D11:
                    // at the moment, only Direct3D11 is working with Kerbal Space Program
                    break;
                default:
                    throw new InvalidOperationException(SystemInfo.graphicsDeviceType.ToString() + " is not supported");
            }

            // initialize render textures (for displaying on HMD)
            for (int i = 0; i < 2; i++) {
                HmdEyeRenderTexture[i] = new RenderTexture((int)renderTextureWidth, (int)renderTextureHeight, 24, RenderTextureFormat.ARGB32);
                HmdEyeRenderTexture[i].Create();

                /**
                 * texture rendering bounds:
                 * uMin = 0f
                 * uMax = 1f
                 * vMin = 1f   flip the vertical coord for some reason (I think it's a D3D11 thing)
                 * vMax = 0f
                 */

                // send the textures to the native renderer plugin
                SetTextureFromUnity(i, HmdEyeRenderTexture[i].GetNativeTexturePtr(), 0f, 1f, 1f, 0f);
            }
        }

        /// <summary>
        /// Sets the tracking space for the HMD
        /// </summary>
        public static void SetHmdTrackingSpace(ETrackingUniverseOrigin origin) {
            if (openVrState == OpenVrState.Initialized) {
                OpenVR.Compositor.SetTrackingSpace(origin);
            }
        }

        /// <summary>
        /// Sets the current real-world position of the HMD as the seated origin.
        /// </summary>
        public static void ResetInitialHmdPosition() {
            if (openVrState == OpenVrState.Initialized) {
                OpenVR.System.ResetSeatedZeroPose();
            }
        }

        /// <summary>
        /// Shuts down the OpenVR API.
        /// </summary>
        protected void CloseVr() {
            VrIsEnabled = false;
            OpenVR.Shutdown();
            openVrState = OpenVrState.Uninitialized;
        }

    } // class Core
} // namespace KerbalVR