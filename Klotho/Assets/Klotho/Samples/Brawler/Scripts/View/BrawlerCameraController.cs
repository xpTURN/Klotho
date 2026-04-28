using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Cinemachine;

namespace Brawler
{
    public class BrawlerCameraController : MonoBehaviour
    {
        [SerializeField] private CinemachineCamera _cinemachineCamera;
        [SerializeField] private Transform _proxy;

        [Header("Zoom")]
        [SerializeField] private float _zoomMin = 12f;
        [SerializeField] private float _zoomMax = 30f;
        [SerializeField] private float _zoomSpeed = 3f;
        [SerializeField] private float _zoomDefault = 20f;

        private CinemachineFollow _followComponent;
        private float _currentZoom;
        private Transform _realTarget;

        private void Awake()
        {
            _followComponent = _cinemachineCamera?.gameObject.GetComponent<CinemachineFollow>();
            _currentZoom = _zoomDefault;

            if (_cinemachineCamera != null)
                _cinemachineCamera.gameObject.SetActive(false);
        }

        private void Update()
        {
            float scroll = Mouse.current?.scroll.ReadValue().y ?? 0f;
            if (Mathf.Abs(scroll) > 0.01f)
                HandleZoomInput(scroll);
        }

        private void LateUpdate()
        {
            if (_realTarget == null) return;
            _proxy.position = new Vector3(_realTarget.position.x, 0f, _realTarget.position.z);
        }

        public void SetFollowTarget(Transform target)
        {
            if (_cinemachineCamera == null) return;
            _realTarget = target;
            _cinemachineCamera.Follow = _proxy;
            _cinemachineCamera.LookAt = _proxy;
            _cinemachineCamera.gameObject.SetActive(true);
        }

        public void ClearFollowTarget()
        {
            if (_cinemachineCamera == null) return;
            _cinemachineCamera.gameObject.SetActive(false);
            _cinemachineCamera.Follow = null;
            _cinemachineCamera.LookAt = null;
            _realTarget = null;
        }

        private void HandleZoomInput(float scrollDelta)
        {
            _currentZoom = Mathf.Clamp(_currentZoom - scrollDelta * _zoomSpeed, _zoomMin, _zoomMax);

            if (_followComponent != null)
            {
                float ratio = _currentZoom / _zoomDefault;
                _followComponent.FollowOffset = new Vector3(0f, _currentZoom, -10f * ratio);
            }
        }
    }
}
