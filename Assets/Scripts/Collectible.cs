using UnityEngine;

namespace MuseumRestorer
{
    /// <summary>
    /// Фрагмент мозаики: подлинник (itemType = 0) или поддельная копия (itemType = 1).
    /// Отрисовывает вращение и пульсацию, передаёт контакт менеджеру игры.
    /// </summary>
    [RequireComponent(typeof(Collider2D))]
    public class Collectible : MonoBehaviour
    {
        public int itemIndex = 1;

        public int itemType = 0;

        private Vector3 startScale;

        private void Awake()
        {
            GetComponent<Collider2D>().isTrigger = true;
            startScale = transform.localScale;
        }

        private void Update()
        {
            transform.Rotate(0f, 0f, 70f * Time.deltaTime);

            float pulse = 1.0f + Mathf.Sin(Time.time * 4.0f) * 0.08f;
            transform.localScale = startScale * pulse;
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (!other.TryGetComponent<PlayerController>(out _))
            {
                return;
            }

            if (GameManager.Instance != null)
            {
                GameManager.Instance.Collect(this);
            }
        }
    }
}
