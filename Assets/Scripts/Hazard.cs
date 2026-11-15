using UnityEngine;

namespace MuseumRestorer
{
    /// <summary>
    /// Зона опасности (сигнализация музейного зала). Оставлена в архитектуре
    /// для соответствия базовому шаблону учебного модуля.
    /// </summary>
    [RequireComponent(typeof(Collider2D))]
    public class Hazard : MonoBehaviour
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
                GameManager.Instance.HitHazard();
            }
        }
    }
}
