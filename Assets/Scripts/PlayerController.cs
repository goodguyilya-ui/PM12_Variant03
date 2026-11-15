using UnityEngine;

namespace MuseumRestorer
{
    /// <summary>
    /// Перемещение робота-реставратора: клавиатура (WASD / стрелки) + экранный D-Pad.
    /// Физика — Rigidbody2D (вид сверху), скорость — линейная скорость тела (Unity 6 API).
    /// </summary>
    [RequireComponent(typeof(Rigidbody2D), typeof(Collider2D))]
    public class PlayerController : MonoBehaviour
    {
        [SerializeField, Min(1f)] private float speed = 5f;

        private Rigidbody2D body;

        private Vector2 startPosition;

        private void Awake()
        {
            body = GetComponent<Rigidbody2D>();
            body.gravityScale = 0f;
            body.freezeRotation = true;

            startPosition = transform.position;
        }

        private void FixedUpdate()
        {
            if (GameManager.Instance == null || !GameManager.Instance.IsPlaying)
            {
                body.linearVelocity = Vector2.zero;
                return;
            }

            float x = ReadAxis(KeyCode.A, KeyCode.LeftArrow, KeyCode.D, KeyCode.RightArrow);
            float y = ReadAxis(KeyCode.S, KeyCode.DownArrow, KeyCode.W, KeyCode.UpArrow);

            Vector2 combined = new Vector2(x, y) + GameManager.Instance.MobileDirection;
            Vector2 direction = Vector2.ClampMagnitude(combined, 1f);

            // Unity 6: Rigidbody2D.linearVelocity вместо устаревшего velocity.
            body.linearVelocity = direction * speed;
        }

        /// <summary>Суммирует отрицательную и положительную оси клавиатуры.</summary>
        private static float ReadAxis(KeyCode negativeA, KeyCode negativeB, KeyCode positiveA, KeyCode positiveB)
        {
            float value = 0f;

            if (Input.GetKey(negativeA) || Input.GetKey(negativeB))
            {
                value -= 1f;
            }

            if (Input.GetKey(positiveA) || Input.GetKey(positiveB))
            {
                value += 1f;
            }

            return value;
        }

        /// <summary>Мгновенно возвращает робота в точку спавна и гасит скорость.</summary>
        public void ReturnToStart()
        {
            EnsureBody();

            body.linearVelocity = Vector2.zero;
            body.angularVelocity = 0f;
            body.position = startPosition;
            transform.position = startPosition;
        }

        /// <summary>Гасит скорость тела, не меняя позицию робота (заморозка в зоне реставрации).</summary>
        public void Stop()
        {
            EnsureBody();

            body.linearVelocity = Vector2.zero;
            body.angularVelocity = 0f;
        }

        private void EnsureBody()
        {
            if (body == null)
            {
                body = GetComponent<Rigidbody2D>();
            }
        }
    }
}
