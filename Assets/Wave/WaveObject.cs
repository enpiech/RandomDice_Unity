using UnityEngine;

namespace Wave
{
    public sealed class WaveObject : MonoBehaviour
    {
        [SerializeField]
        private int _wave;

        [SerializeField]
        private TextMesh _waveText;

        [SerializeField]
        private float _timer;

        [SerializeField]
        private TextMesh _timerText;

        [SerializeField]
        private float _timerInterval = 1f;

        private float _deltaTimer;

        public int Wave => _wave;

        public float Timer => _timer;

        private void Awake()
        {
            _deltaTimer = 0;
        }

        private void Update()
        {
            UpdateWaveText();

            UpdateTimerText();

            CountTimer();
        }

        private void CountTimer()
        {
            _deltaTimer += Time.deltaTime;
            if (_deltaTimer >= _timerInterval)
            {
                _timer -= _timerInterval;
                _deltaTimer -= _timerInterval;
            }

            if (_timer < 0)
            {
                _timer = 0;
            }
        }

        private void UpdateTimerText()
        {
            _timerText.text = $"{Mathf.FloorToInt(_timer / 60f):D2}:{_timer % 60:D2}";
        }

        private void UpdateWaveText()
        {
            _waveText.text = _wave.ToString();
        }
    }
}