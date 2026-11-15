namespace MuseumRestorer
{
    /// <summary>
    /// Чистая предметная модель механики «Выборочный сбор» (Selection).
    /// POCO-класс, не зависящий от UnityEngine: пригоден для unit-тестов.
    /// </summary>
    public static class MechanicModels
    {
        /// <summary>
        /// Состояние механики: два независимых регистра — успешные подборы и ошибки анализа.
        /// </summary>
        public sealed class Selection
        {
            /// <summary>Количество собранных подлинников (цель — 5).</summary>
            public int Progress { get; private set; }

            /// <summary>Количество зафиксированных ошибок (лимит — 3).</summary>
            public int Errors { get; private set; }

            /// <summary>
            /// Регистрирует попытку взять предмет.
            /// </summary>
            /// <param name="correct">true — подлинник (itemType == 0), false — копия (itemType == 1).</param>
            /// <returns>true при успешном подборе, false при ошибке.</returns>
            public bool Take(bool correct)
            {
                if (!correct)
                {
                    Errors++;
                    return false;
                }

                Progress++;
                return true;
            }

            /// <summary>Обнуляет оба регистра для детерминированного рестарта.</summary>
            public void Reset()
            {
                Progress = 0;
                Errors = 0;
            }
        }
    }
}
