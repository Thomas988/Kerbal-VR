using System;
using System.Collections.Generic;
using UnityEngine;
using Valve.VR;

namespace KerbalVR
{
    /// <summary>
    /// Scene is a singleton class that encapsulates the code that positions
    /// the game cameras correctly for rendering them to the VR headset,
    /// according to the current KSP scene (flight, editor, etc).
    /// </summary>
    public class Scene : MonoBehaviour
    {
        #region Constants
        public static readonly string[] FLIGHT_SCENE_IVA_CAMERAS = {
            "GalaxyCamera",
            "Camera ScaledSpace",
            "Camera 00",
            "InternalCamera",
        };

        public static readonly string[] FLIGHT_SCENE_EVA_CAMERAS = {
            "GalaxyCamera",
            "Camera ScaledSpace",
            "Camera 00",
        };

        public static readonly string[] SPACECENTER_SCENE_CAMERAS = {
            "GalaxyCamera",
            "Camera ScaledSpace",
            "Camera 00",
        };

        public static readonly string[] EDITOR_SCENE_CAMERAS = {
            "GalaxyCamera",
            "sceneryCam",
            "Main Camera",
            "markerCam",
        };

        public static readonly string[] MAINMENU_SCENE_CAMERAS = {
            "GalaxyCamera",
            "Landscape Camera",
        };
        #endregion

        #region Singleton
        // this is a singleton class, and there must be one Scene in the scene
        private static Scene _instance;
        public static Scene Instance {
            get {
                if (_instance == null) {
                    _instance = FindObjectOfType<Scene>();
                    if (_instance == null) {
                        Utils.LogError("The scene needs to have one active GameObject with a Scene script attached!");
                    } else {
                        _instance.Initialize();
                    }
                }
                return _instance;
            }
        }

        // first-time initialization for this singleton class
        private void Initialize() {
            HmdEyePosition = new Vector3[2];
            HmdEyeRotation = new Quaternion[2];

            // initialize world scale values for each Game Scene
            inverseWorldScale = new Dictionary<GameScenes, float>();
            Array gameScenes = Enum.GetValues(typeof(GameScenes));
            foreach (GameScenes scene in gameScenes) {
                inverseWorldScale.Add(scene, 1f);
            }
        }
        #endregion


        #region Properties
        // The list of cameras to render for the current scene.
        public Types.CameraData[] VRCameras { get; private set; }
        public int NumVRCameras { get; private set; }

        // The initial world position of the cameras for the current scene. This
        // position corresponds to the origin in the real world physical device
        // coordinate system.
        public Vector3 InitialPosition { get; private set; }
        public Quaternion InitialRotation { get; private set; }

        // The current world position of the cameras for the current scene. This
        // position corresponds to the origin in the real world physical device
        // coordinate system.
        public Vector3 CurrentPosition { get; set; }
        public Quaternion CurrentRotation { get; set; }

        /// <summary>
        /// The current position of the HMD in Unity world coordinates
        /// </summary>
        public Vector3 HmdPosition { get; private set; }
        /// <summary>
        /// The current rotation of the HMD in Unity world coordinates
        /// </summary>
        public Quaternion HmdRotation { get; private set; }

        /// <summary>
        /// The current position of the HMD eye in Unity world coordinates,
        /// indexed by EVREye value.
        /// </summary>
        public Vector3[] HmdEyePosition { get; private set; }
        /// <summary>
        /// The current rotation of the HMD left eye in Unity world coordinates,
        /// indexed by EVREye value.
        /// </summary>
        public Quaternion[] HmdEyeRotation { get; private set; }

        // defines the tracking method to use
        public ETrackingUniverseOrigin TrackingSpace { get; private set; }

        // defines what layer to render KerbalVR objects on
        public int RenderLayer { get; private set; } = 0;

        // defines the world scaling factor (store the inverse)
        public float WorldScale {
            get { return (1f / inverseWorldScale[HighLogic.LoadedScene]); }
            set { inverseWorldScale[HighLogic.LoadedScene] = (1f / value); }
        }

        public Camera KspUiCamera { get; private set; } = null;
        public Color KspUiCameraBackgroundColor { get; private set; }
        public CameraClearFlags KspUiCameraClearFlags { get; private set; }
        #endregion


        #region Private Members
        private Dictionary<GameScenes, float> inverseWorldScale;
        private float editorMovementSpeed = 1f;
        private GameObject galaxyCamera = null;
        private GameObject landscapeCamera = null;
        private MainMenuEnvLogic mainMenuLogic = null;

        private GameObject mainMenuUiScreen = null;
        #endregion


        void OnEnable() {
            Events.ManipulatorLeftUpdated.Listen(OnManipulatorLeftUpdated);
            Events.ManipulatorRightUpdated.Listen(OnManipulatorRightUpdated);
        }

        void OnDisable() {
            Events.ManipulatorLeftUpdated.Remove(OnManipulatorLeftUpdated);
            Events.ManipulatorRightUpdated.Remove(OnManipulatorRightUpdated);
        }


        /// <summary>
        /// Set up the list of cameras to render for this scene and the initial position
        /// corresponding to the origin in the real world device coordinate system.
        /// </summary>
        public void SetupScene() {
            // capture the UI camera
            GameObject kspUiCameraGameObject = GameObject.Find("UIMainCamera");
            if (kspUiCameraGameObject != null) {
                KspUiCamera = kspUiCameraGameObject.GetComponent<Camera>();
            }
            if (KspUiCamera == null) {
                Utils.LogError("Could not find UIMainCamera component!");
            } else {
                KspUiCameraBackgroundColor = KspUiCamera.backgroundColor;
                KspUiCameraClearFlags = KspUiCamera.clearFlags;
            }

            // set up game-scene-specific cameras
            switch (HighLogic.LoadedScene) {
                case GameScenes.MAINMENU:
                    SetupMainMenuScene();
                    break;

                case GameScenes.FLIGHT:
                    if (CameraManager.Instance.currentCameraMode == CameraManager.CameraMode.IVA) {
                        SetupFlightIvaScene();
                    } else if (FlightGlobals.ActiveVessel.isEVA) {
                        SetupFlightEvaScene();
                    }
                    break;

                case GameScenes.EDITOR:
                    SetupEditorScene();
                    break;

                default:
                    throw new Exception("Cannot setup VR scene, current scene \"" +
                        HighLogic.LoadedScene + "\" is invalid.");
            }

            CurrentPosition = InitialPosition;
            CurrentRotation = InitialRotation;
        }

        private void SetupMainMenuScene() {
            // use seated mode during main menu
            TrackingSpace = ETrackingUniverseOrigin.TrackingUniverseSeated;

            // render KerbalVR objects on the default layer
            RenderLayer = 0;

            // generate list of cameras to render
            PopulateCameraList(MAINMENU_SCENE_CAMERAS);

            // cache the menu logic object
            mainMenuLogic = GameObject.FindObjectOfType<MainMenuEnvLogic>();
            mainMenuLogic.fadeEndDistance = 10f;

            // set inital scene position
            InitialPosition = Vector3.zero;
            InitialRotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);

            // create a UI screen
            if (mainMenuUiScreen == null) {
                mainMenuUiScreen = GameObject.CreatePrimitive(PrimitiveType.Quad);
                mainMenuUiScreen.name = "KVR_KSP_UI_Screen";
                mainMenuUiScreen.transform.position = CurrentPosition + new Vector3(0.4f, 0f, 0f);
                mainMenuUiScreen.transform.rotation = Quaternion.Euler(0f, 30f, 0f);
                Vector3 uiScreenScale = Vector3.one * 0.6f;
                uiScreenScale.x = uiScreenScale.y * (16f / 9f);
                mainMenuUiScreen.transform.localScale = uiScreenScale;
                MeshRenderer mr = mainMenuUiScreen.GetComponent<MeshRenderer>();
                mr.material = new Material(Shader.Find("KSP/Alpha/Unlit Transparent"));
                mr.material.mainTexture = KerbalVR.Core.KspUiRenderTexture;
            }
        }

        private void SetupFlightIvaScene() {
            // use seated mode during IVA flight
            TrackingSpace = ETrackingUniverseOrigin.TrackingUniverseSeated;

            // render KerbalVR objects on the InternalSpace layer
            RenderLayer = 20;

            // generate list of cameras to render
            PopulateCameraList(FLIGHT_SCENE_IVA_CAMERAS);

            // set inital scene position
            InitialPosition = InternalCamera.Instance.transform.position;

            // set rotation to always point forward inside the cockpit
            // NOTE: actually this code doesn't work for certain capsules
            // with different internal origin orientations
            /*InitialRotation = Quaternion.LookRotation(
                InternalSpace.Instance.transform.rotation * Vector3.up,
                InternalSpace.Instance.transform.rotation * Vector3.back);*/

            InitialRotation = InternalCamera.Instance.transform.rotation;
        }

        private void SetupFlightEvaScene() {
            // use seated mode during EVA
            TrackingSpace = ETrackingUniverseOrigin.TrackingUniverseSeated;

            // render KerbalVR objects on the InternalSpace layer
            RenderLayer = 20;

            // generate list of cameras to render
            PopulateCameraList(FLIGHT_SCENE_EVA_CAMERAS);

            // set inital scene position
            InitialPosition = FlightGlobals.ActiveVessel.transform.position;

            // set rotation to always point forward inside the cockpit
            // NOTE: actually this code doesn't work for certain capsules
            // with different internal origin orientations
            /*InitialRotation = Quaternion.LookRotation(
                InternalSpace.Instance.transform.rotation * Vector3.up,
                InternalSpace.Instance.transform.rotation * Vector3.back);*/

            InitialRotation = FlightGlobals.ActiveVessel.transform.rotation;
        }

        private void SetupEditorScene() {
            // use room-scale in editor
            TrackingSpace = ETrackingUniverseOrigin.TrackingUniverseStanding;

            // render KerbalVR objects on the default layer
            RenderLayer = 0;

            // generate list of cameras to render
            PopulateCameraList(EDITOR_SCENE_CAMERAS);

            // set inital scene position

            //Vector3 forwardDir = EditorCamera.Instance.transform.rotation * Vector3.forward;
            //forwardDir.y = 0f; // make the camera point straight forward
            //Vector3 startingPos = EditorCamera.Instance.transform.position;
            //startingPos.y = 0f; // start at ground level

            Vector3 startingPos = new Vector3(0f, 0f, -5f);

            InitialPosition = startingPos;
            InitialRotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
        }

        /// <summary>
        /// Updates the game cameras to the correct position, according to the given HMD eye pose.
        /// </summary>
        /// <param name="eyePosition">Position of the HMD eye, in the device space coordinate system</param>
        /// <param name="eyeRotation">Rotation of the HMD eye, in the device space coordinate system</param>
        public void UpdateScene(
            EVREye eye,
            SteamVR_Utils.RigidTransform hmdTransform,
            SteamVR_Utils.RigidTransform hmdEyeTransform) {

            switch (HighLogic.LoadedScene) {
                case GameScenes.MAINMENU:
                    UpdateMainMenuScene(eye, hmdTransform, hmdEyeTransform);
                    break;

                case GameScenes.FLIGHT:
                    if (CameraManager.Instance.currentCameraMode == CameraManager.CameraMode.IVA) {
                        UpdateFlightIvaScene(eye, hmdTransform, hmdEyeTransform);
                    } else if (FlightGlobals.ActiveVessel.isEVA) {
                        UpdateFlightEvaScene(eye, hmdTransform, hmdEyeTransform);
                    }
                    break;

                case GameScenes.EDITOR:
                    UpdateEditorScene(eye, hmdTransform, hmdEyeTransform);
                    break;

                default:
                    throw new Exception("Cannot setup VR scene, current scene \"" +
                        HighLogic.LoadedScene + "\" is invalid.");
            }

            HmdPosition = CurrentPosition + CurrentRotation * hmdTransform.pos;
            HmdRotation = CurrentRotation * hmdTransform.rot;
        }

        private void UpdateMainMenuScene(
            EVREye eye,
            SteamVR_Utils.RigidTransform hmdTransform,
            SteamVR_Utils.RigidTransform hmdEyeTransform) {

            // lock in the initial rotation
            CurrentRotation = InitialRotation;

            // position should be based on where we need to look at the main menu. need
            // to keep track of when the stage position changes
            CurrentPosition = Vector3.MoveTowards(CurrentPosition, mainMenuLogic.camPivots[mainMenuLogic.currentStage].targetPoint.position, 0.1f);

            // get position of your eyeball
            // Vector3 positionToHmd = hmdTransform.pos;
            Vector3 positionToEye = hmdTransform.pos + hmdTransform.rot * hmdEyeTransform.pos;

            // translate device space to Unity space, with world scaling
            Vector3 updatedPosition = DevicePoseToWorld(positionToEye);
            Quaternion updatedRotation = DevicePoseToWorld(hmdTransform.rot);

            // update the menu scene
            landscapeCamera.transform.position = updatedPosition;
            landscapeCamera.transform.rotation = updatedRotation;

            // update the sky cameras
            galaxyCamera.transform.rotation = updatedRotation;

            // store the eyeball position
            HmdEyePosition[(int)eye] = updatedPosition;
            HmdEyeRotation[(int)eye] = updatedRotation;

            // update the UI screen
            mainMenuUiScreen.transform.position = CurrentPosition + new Vector3(1f, 0f, 1f);
        }

        private void UpdateFlightIvaScene(
            EVREye eye,
            SteamVR_Utils.RigidTransform hmdTransform,
            SteamVR_Utils.RigidTransform hmdEyeTransform) {

            // in flight, don't allow movement of the origin point
            CurrentPosition = InitialPosition;
            CurrentRotation = InitialRotation;

            // get position of your eyeball
            // Vector3 positionToHmd = hmdTransform.pos;
            Vector3 positionToEye = hmdTransform.pos + hmdTransform.rot * hmdEyeTransform.pos;

            // translate device space to Unity space, with world scaling
            Vector3 updatedPosition = DevicePoseToWorld(positionToEye);
            Quaternion updatedRotation = DevicePoseToWorld(hmdTransform.rot);
            Vector3 updatedWorldPosition = InternalSpace.InternalToWorld(updatedPosition);
            Quaternion updatedWorldRotation = InternalSpace.InternalToWorld(updatedRotation);

            // in flight, update the internal and flight cameras
            InternalCamera.Instance.transform.position = updatedPosition;
            InternalCamera.Instance.transform.rotation = updatedRotation;

            FlightCamera.fetch.transform.position = updatedWorldPosition;
            FlightCamera.fetch.transform.rotation = updatedWorldRotation;

            // update the sky cameras
            ScaledCamera.Instance.transform.rotation = updatedWorldRotation;
            galaxyCamera.transform.rotation = updatedWorldRotation;

            // store the eyeball position
            HmdEyePosition[(int)eye] = updatedPosition;
            HmdEyeRotation[(int)eye] = updatedRotation;
        }

        private void UpdateFlightEvaScene(
            EVREye eye,
            SteamVR_Utils.RigidTransform hmdTransform,
            SteamVR_Utils.RigidTransform hmdEyeTransform) {

            // in flight, don't allow movement of the origin point
            CurrentPosition = InitialPosition;
            CurrentRotation = InitialRotation;

            // get position of your eyeball
            Vector3 positionToHmd = hmdTransform.pos;
            Vector3 positionToEye = hmdTransform.pos + hmdTransform.rot * hmdEyeTransform.pos;

            // translate device space to Unity space, with world scaling
            Vector3 updatedPosition = DevicePoseToWorld(positionToEye);
            Quaternion updatedRotation = DevicePoseToWorld(hmdTransform.rot);

            // in flight, update the flight cameras
            FlightCamera.fetch.transform.position = updatedPosition;
            FlightCamera.fetch.transform.rotation = updatedRotation;

            ScaledCamera.Instance.transform.rotation = updatedRotation;
            galaxyCamera.transform.rotation = updatedRotation;

            // store the eyeball position
            HmdEyePosition[(int)eye] = updatedPosition;
            HmdEyeRotation[(int)eye] = updatedRotation;
        }

        private void UpdateEditorScene(
            EVREye eye,
            SteamVR_Utils.RigidTransform hmdTransform,
            SteamVR_Utils.RigidTransform hmdEyeTransform) {

            // get position of your eyeball
            Vector3 positionToHmd = hmdTransform.pos;
            Vector3 positionToEye = hmdTransform.pos + hmdTransform.rot * hmdEyeTransform.pos;

            // translate device space to Unity space, with world scaling
            Vector3 updatedPosition = DevicePoseToWorld(positionToEye);
            Quaternion updatedRotation = DevicePoseToWorld(hmdTransform.rot);

            // update the editor camera position
            EditorCamera.Instance.transform.position = updatedPosition;
            EditorCamera.Instance.transform.rotation = updatedRotation;

            // store the eyeball position
            HmdEyePosition[(int)eye] = updatedPosition;
            HmdEyeRotation[(int)eye] = updatedRotation;
        }

        /// <summary>
        /// Resets game cameras back to their original settings
        /// </summary>
        public void CloseScene() {
            // reset cameras to their original settings
            if (VRCameras != null) {
                for (int i = 0; i < VRCameras.Length; i++) {
                    VRCameras[i].camera.targetTexture = null;
                    VRCameras[i].camera.projectionMatrix = VRCameras[i].originalProjectionMatrix;
                    VRCameras[i].camera.enabled = true;
                }
            }
        }

        /// <summary>
        /// Populates the list of cameras according to the cameras that should be used for
        /// the current game scene.
        /// </summary>
        /// <param name="cameraNames">An array of camera names to use for this VR scene.</param>
        private void PopulateCameraList(string[] cameraNames) {
            // search for the cameras to render
            NumVRCameras = cameraNames.Length;
            VRCameras = new Types.CameraData[NumVRCameras];
            for (int i = 0; i < NumVRCameras; i++) {
                Camera foundCamera = Array.Find(Camera.allCameras, cam => cam.name.Equals(cameraNames[i]));
                if (foundCamera == null) {
                    Utils.LogError("Could not find camera \"" + cameraNames[i] + "\" in the scene!");

                } else {
                    // determine clip plane and new projection matrices
                    HmdMatrix44_t projectionMatrixL = OpenVR.System.GetProjectionMatrix(EVREye.Eye_Left, foundCamera.nearClipPlane, foundCamera.farClipPlane);
                    HmdMatrix44_t projectionMatrixR = OpenVR.System.GetProjectionMatrix(EVREye.Eye_Right, foundCamera.nearClipPlane, foundCamera.farClipPlane);

                    // store information about the camera
                    VRCameras[i].camera = foundCamera;
                    VRCameras[i].originalProjectionMatrix = foundCamera.projectionMatrix;
                    VRCameras[i].hmdProjectionMatrixL = MathUtils.Matrix4x4_OpenVr2UnityFormat(ref projectionMatrixL);
                    VRCameras[i].hmdProjectionMatrixR = MathUtils.Matrix4x4_OpenVr2UnityFormat(ref projectionMatrixR);

                    // disable the camera so we can call Render directly
                    foundCamera.enabled = false;

                    // cache the galaxy camera object, we'll need to call on it directly during eyeball positioning
                    if (foundCamera.name == "GalaxyCamera") {
                        galaxyCamera = foundCamera.gameObject;
                    } else if (foundCamera.name == "Landscape Camera") {
                        landscapeCamera = foundCamera.gameObject;
                    }
                }
            }
        }

        public bool SceneAllowsVR() {
            bool allowed = false;
            switch (HighLogic.LoadedScene) {
                case GameScenes.MAINMENU:
                    allowed = true;
                    break;

                case GameScenes.FLIGHT:
                    allowed = ((CameraManager.Instance != null) && (CameraManager.Instance.currentCameraMode == CameraManager.CameraMode.IVA)) ||
                        ((FlightGlobals.ActiveVessel != null) && (FlightGlobals.ActiveVessel.isEVA));
                    break;

                case GameScenes.EDITOR:
                    allowed = true;
                    break;

                default:
                    allowed = false;
                    break;
            }
            return allowed;
        }

        /// <summary>
        /// Convert a device position to Unity world coordinates for this scene.
        /// </summary>
        /// <param name="devicePosition">Device position in the device space coordinate system.</param>
        /// <returns>Unity world position corresponding to the device position.</returns>
        public Vector3 DevicePoseToWorld(Vector3 devicePosition) {
            return CurrentPosition + CurrentRotation *
                (devicePosition * inverseWorldScale[HighLogic.LoadedScene]);
        }

        /// <summary>
        /// Convert a device rotation to Unity world coordinates for this scene.
        /// </summary>
        /// <param name="deviceRotation">Device rotation in the device space coordinate system.</param>
        /// <returns>Unity world rotation corresponding to the device rotation.</returns>
        public Quaternion DevicePoseToWorld(Quaternion deviceRotation) {
            return CurrentRotation * deviceRotation;
        }

        public void OnManipulatorLeftUpdated(SteamVR_Controller.Device state) {
            // left touchpad
            if (state.GetPress(EVRButtonId.k_EButton_SteamVR_Touchpad)) {
                Vector2 touchAxis = state.GetAxis(EVRButtonId.k_EButton_SteamVR_Touchpad);

                Vector3 upDisplacement = Vector3.up *
                    (editorMovementSpeed * inverseWorldScale[HighLogic.LoadedScene] * touchAxis.y) * Time.deltaTime;

                Vector3 newPosition = CurrentPosition + upDisplacement;
                if (newPosition.y < 0f) newPosition.y = 0f;

                CurrentPosition = newPosition;
            }

            // left menu button
            if (state.GetPressDown(EVRButtonId.k_EButton_ApplicationMenu)) {
                Core.ResetInitialHmdPosition();
            }

            // simulate mouse touch events with the trigger
            if (state.GetPressDown(EVRButtonId.k_EButton_SteamVR_Trigger)) {
                foreach (var obj in DeviceManager.Instance.ManipulatorLeft.FingertipCollidedGameObjects) {
                    if (obj != null) obj.SendMessage("OnMouseDown");
                }
            }

            if (state.GetPressUp(EVRButtonId.k_EButton_SteamVR_Trigger)) {
                foreach (var obj in DeviceManager.Instance.ManipulatorLeft.FingertipCollidedGameObjects) {
                    if (obj != null) obj.SendMessage("OnMouseUp");
                }
            }
        }

        public void OnManipulatorRightUpdated(SteamVR_Controller.Device state) {
            // right touchpad
            if (state.GetPress(EVRButtonId.k_EButton_SteamVR_Touchpad)) {
                Vector2 touchAxis = state.GetAxis(EVRButtonId.k_EButton_SteamVR_Touchpad);

                Vector3 fwdDirection = HmdRotation * Vector3.forward;
                fwdDirection.y = 0f; // allow only planar movement
                Vector3 fwdDisplacement = fwdDirection.normalized *
                    (editorMovementSpeed * inverseWorldScale[HighLogic.LoadedScene] * touchAxis.y) * Time.deltaTime;

                Vector3 rightDirection = HmdRotation * Vector3.right;
                rightDirection.y = 0f; // allow only planar movement
                Vector3 rightDisplacement = rightDirection.normalized *
                    (editorMovementSpeed * inverseWorldScale[HighLogic.LoadedScene] * touchAxis.x) * Time.deltaTime;

                CurrentPosition += fwdDisplacement + rightDisplacement;
            }

            // right menu button
            if (state.GetPressDown(EVRButtonId.k_EButton_ApplicationMenu)) {
                Core.ResetInitialHmdPosition();
            }

            // simulate mouse touch events with the trigger
            if (state.GetPressDown(EVRButtonId.k_EButton_SteamVR_Trigger)) {
                foreach (var obj in DeviceManager.Instance.ManipulatorRight.FingertipCollidedGameObjects) {
                    if (obj != null) obj.SendMessage("OnMouseDown");
                }
            }

            if (state.GetPressUp(EVRButtonId.k_EButton_SteamVR_Trigger)) {
                foreach (var obj in DeviceManager.Instance.ManipulatorRight.FingertipCollidedGameObjects) {
                    if (obj != null) obj.SendMessage("OnMouseUp");
                }
            }
        }
    } // class Scene
} // namespace KerbalVR
