using UnityEngine;

namespace MuseumRestorer
{
    /// <summary>
    /// Сенсорная зона реставрационного монтажного стола (Exit).
    /// Завершает уровень только при полном комплекте подлинников.
    /// </summary>
    [RequireComponent(typeof(Collider2D))]
    public class ExitZone : MonoBehaviour
    {
        private void Awake()
        {
            GetComponent<Collider2D>().isTrigger = true;
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (!other.TryGetComponent<PlayerController>(out _))
            {
                return;
            }

            if (GameManager.Instance != null)
            {
                GameManager.Instance.TryFinishLevel();
            }
        }
    }
}
