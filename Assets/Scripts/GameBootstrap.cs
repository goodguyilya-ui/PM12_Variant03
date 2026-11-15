using UnityEngine;

namespace MuseumRestorer
{
    /// <summary>
    /// Точка входа в игру без ручного добавления скриптов на сцену.
    /// После загрузки любой сцены (в т.ч. пустой Main.unity) создаёт объект GameManager,
    /// который процедурно строит весь уровень.
    /// </summary>
    public static class GameBootstrap
    {
        /// <summary>
        /// Вызывается редактором/игрой автоматически после загрузки сцены.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void CreateGame()
        {
            // Защита от повторной инициализации (перезагрузка сцены, доменный перезапуск).
            if (Object.FindAnyObjectByType<GameManager>() != null)
            {
                return;
            }

            GameObject root = new GameObject("MuseumRestorer");
            root.AddComponent<GameManager>();
        }
    }
}
