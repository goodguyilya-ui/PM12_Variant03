using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MuseumRestorer
{
    /// <summary>
    /// Главный диспетчер уровня: процедурная генерация геометрии и спрайтов,
    /// машина состояний, обработка ввода и отрисовка интерфейса OnGUI.
    ///
    /// При каждом запуске/рестарте (RestartGame):
    ///   • случайно выбирается одна из 4 проверенных проходимых планировок зала;
    ///   • случайно выбирается одна из 3 цветовых тем музея;
    ///   • 5 подлинников и 4 копии раскладываются по точкам расширенного пула,
    ///     очищенного от позиций рядом с текущими внутренними витринами.
    /// Победа засчитывается только после доставки подлинников в зону реставрации.
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        private enum GameState { Playing, Won, Lost }

        private enum Shape { Square, Circle, Diamond, Cross, Exit, AuthenticMosaic, FakeMosaic, Pedestal }

        /// <summary>Цветовая тема музейного зала.</summary>
        private struct MuseumTheme
        {
            public string Name;
            public Color FloorBase;
            public Color FloorTile;
            public Color WallOuter;
            public Color WallInner;
            public Color WallTrim;
            public Color Pedestal;
            public Color Ambient;
        }

        /// <summary>Одна внутренняя витрина/перегородка планировки зала.</summary>
        private struct WallSpec
        {
            public string Name;
            public Vector2 Position;
            public Vector2 Size;

            public WallSpec(string name, float x, float y, float w, float h)
            {
                Name = name;
                Position = new Vector2(x, y);
                Size = new Vector2(w, h);
            }
        }

        private const int Target = 5;
        private const int MaxErrors = 3;

        private const float OrthoSize = 5.3f;
        private const float PixelPerUnit = 128f;

        private const float PlayerStartX = -6.5f;
        private const float PlayerStartY = -3.1f;
        private const float ExitX = 6.5f;
        private const float ExitY = 3.15f;

        private const float PlayerScale = 0.95f;
        private const float ItemScale = 0.85f;
        private const float ExitScale = 1.35f;

        // Минимальный зазор от точки спавна до границы любой внутренней витрины.
        private const float ItemWallClearance = 0.85f;

        // Минимальная дистанция точки спавна от старта робота и от центра выхода.
        private const float ItemAnchorClearance = 1.3f;

        private static readonly int ReferenceWidth = 1280;
        private static readonly int ReferenceHeight = 720;

        /// <summary>Синглтон-доступ к менеджеру игры.</summary>
        public static GameManager Instance { get; private set; }

        /// <summary>Индикатор активного игрового процесса.</summary>
        public bool IsPlaying => state == GameState.Playing;

        /// <summary>Вектор экранного джойстика (D-Pad).</summary>
        public Vector2 MobileDirection { get; private set; }

        private readonly MechanicModels.Selection selection = new MechanicModels.Selection();

        private readonly List<GameObject> createdObjects = new List<GameObject>();

        // Активные внутренние витрины текущей планировки (стена + молдинг). Очищаются в RestartGame.
        private readonly List<GameObject> innerWalls = new List<GameObject>();

        // ------------------------------------------------------------------
        // 4 заранее проверенные проходимые планировки зала (Random.Range(0, 4)).
        // Проверено численно для всех вариантов:
        //   • старт робота (-6.5, -3.1) и зона выхода (6.5, 3.15) не перекрываются;
        //   • путь от старта к выходу существует (BFS по свободному пространству);
        //   • минимальный зазор между перегородкой и стеной/соседом ≥ 1.6 у.е.,
        //     что больше диаметра робота (0.88), т.е. пролезть и застрять нельзя;
        //   • в каждом варианте остаётся ≥ 97 валидных точек спавна из 18-точечного пула.
        // ------------------------------------------------------------------
        private readonly WallSpec[][] layouts =
        {
            // 0 — «Классика»: вертикальный пилон, горизонтальная витрина, ограничитель шлюза.
            new[]
            {
                new WallSpec("WallA", -3.8f, 1.2f, 0.35f, 3.0f),
                new WallSpec("WallB", 1.4f, -2.2f, 3.2f, 0.35f),
                new WallSpec("WallC", 4.4f, 2.0f, 0.35f, 2.5f)
            },

            // 1 — «Колонный зал»: 4 симметричные колонны-витрины.
            new[]
            {
                new WallSpec("ColumnNW", -3.5f, 1.8f, 1.4f, 1.4f),
                new WallSpec("ColumnSW", -3.5f, -1.5f, 1.4f, 1.4f),
                new WallSpec("ColumnNE", 2.5f, 1.8f, 1.4f, 1.4f),
                new WallSpec("ColumnSE", 2.5f, -1.5f, 1.4f, 1.4f)
            },

            // 2 — «Лабиринт галереи»: три перегородки в шахматном порядке.
            new[]
            {
                new WallSpec("GalleryWest", -4.2f, -0.5f, 0.35f, 3.6f),
                new WallSpec("GalleryNorth", 0.0f, 1.8f, 3.4f, 0.35f),
                new WallSpec("GalleryEast", 3.6f, -1.2f, 0.35f, 3.4f)
            },

            // 3 — «Центральный павильон»: 4 перегородки с широкими проходами.
            new[]
            {
                new WallSpec("PavilionNW", -2.2f, 1.6f, 2.2f, 0.35f),
                new WallSpec("PavilionNE", 2.2f, 1.6f, 2.2f, 0.35f),
                new WallSpec("PavilionSW", -2.2f, -1.6f, 2.2f, 0.35f),
                new WallSpec("PavilionSE", 2.2f, -1.6f, 2.2f, 0.35f)
            }
        };

        private readonly string[] layoutNames =
        {
            "Классика",
            "Колонный зал",
            "Лабиринт галереи",
            "Центральный павильон"
        };

        private int layoutIndex = -1;

        // ------------------------------------------------------------------
        // Расширенный пул из 18 безопасных координат спавна по всему залу.
        // X ∈ [-7.2, 7.2], Y ∈ [-3.65, 3.65], каждая точка удалена не менее
        // чем на 1.3 и от старта робота, и от центра зоны реставрации.
        // Из пула отбираются позиции, удалённые от активных внутренних витрин
        // на ItemWallClearance (после фильтрации остаётся 16–18 точек).
        // ------------------------------------------------------------------
        private readonly Vector2[] spawnPool =
        {
            new Vector2(-7.20f,  3.65f), // Q01
            new Vector2(-5.40f,  3.65f), // Q02
            new Vector2(-3.60f,  3.65f), // Q03
            new Vector2(-1.80f,  3.65f), // Q04
            new Vector2( 0.00f,  3.65f), // Q05
            new Vector2( 1.80f,  3.65f), // Q06
            new Vector2( 3.60f,  3.65f), // Q07
            new Vector2( 7.20f,  1.80f), // Q08
            new Vector2( 7.20f,  0.00f), // Q09
            new Vector2( 3.60f,  0.00f), // Q10
            new Vector2( 1.80f,  0.00f), // Q11
            new Vector2(-0.90f,  0.00f), // Q12
            new Vector2(-3.60f,  0.00f), // Q13
            new Vector2(-6.30f,  0.00f), // Q14
            new Vector2(-1.80f, -3.65f), // Q15
            new Vector2( 2.70f, -3.65f), // Q16
            new Vector2( 5.40f, -3.65f), // Q17
            new Vector2( 7.20f, -3.65f)  // Q18
        };

        // Рабочие списки отфильтрованных и перемешанных позиций.
        private readonly List<Vector2> validSpawnPoints = new List<Vector2>();
        private readonly List<Vector2> shuffledSpawn = new List<Vector2>();

        // Аварийный резерв: заведомо свободные точки у краёв зала (используется, только если
        // после фильтрации осталось меньше 9 точек). На практике не задействуется.
        private readonly Vector2[] fallbackSpawn =
        {
            new Vector2(-7.20f, -3.65f),
            new Vector2( 7.20f, -1.80f),
            new Vector2(-5.40f, -1.80f),
            new Vector2(-1.80f,  1.80f),
            new Vector2( 1.80f, -1.80f),
            new Vector2( 5.40f, -1.80f),
            new Vector2( 3.60f,  3.65f),
            new Vector2( 5.40f,  0.00f),
            new Vector2( 0.00f, -1.80f)
        };

        // ------------------------------------------------------------------
        // Три цветовые темы музейного зала.
        // ------------------------------------------------------------------
        private readonly MuseumTheme[] themes =
        {
            new MuseumTheme
            {
                Name = "Античный сапфир",
                FloorBase = new Color(0.06f, 0.08f, 0.15f),
                FloorTile = new Color(0.11f, 0.14f, 0.24f),
                WallOuter = new Color(0.20f, 0.24f, 0.34f),
                WallInner = new Color(0.15f, 0.27f, 0.46f),
                WallTrim = new Color(0.45f, 0.64f, 0.90f),
                Pedestal = new Color(0.13f, 0.18f, 0.30f),
                Ambient = new Color(0.02f, 0.03f, 0.07f)
            },
            new MuseumTheme
            {
                Name = "Императорский бордо",
                FloorBase = new Color(0.11f, 0.06f, 0.07f),
                FloorTile = new Color(0.17f, 0.10f, 0.11f),
                WallOuter = new Color(0.27f, 0.20f, 0.19f),
                WallInner = new Color(0.38f, 0.15f, 0.17f),
                WallTrim = new Color(0.68f, 0.38f, 0.30f),
                Pedestal = new Color(0.24f, 0.12f, 0.13f),
                Ambient = new Color(0.05f, 0.02f, 0.03f)
            },
            new MuseumTheme
            {
                Name = "Изумрудный зал",
                FloorBase = new Color(0.05f, 0.10f, 0.08f),
                FloorTile = new Color(0.09f, 0.16f, 0.13f),
                WallOuter = new Color(0.19f, 0.26f, 0.22f),
                WallInner = new Color(0.11f, 0.32f, 0.22f),
                WallTrim = new Color(0.42f, 0.78f, 0.56f),
                Pedestal = new Color(0.10f, 0.21f, 0.16f),
                Ambient = new Color(0.02f, 0.05f, 0.04f)
            }
        };

        private int themeIndex = -1;
        private MuseumTheme theme;

        // Витрины для смены темы на лету (без пересоздания текстур).
        private Camera gameCamera;
        private SpriteRenderer floorRenderer;
        private readonly List<SpriteRenderer> tileRenderers = new List<SpriteRenderer>();
        private readonly List<float> tileShades = new List<float>();
        private readonly List<SpriteRenderer> outerWallRenderers = new List<SpriteRenderer>();
        private readonly List<SpriteRenderer> trimRenderers = new List<SpriteRenderer>();
        private readonly List<SpriteRenderer> pedestalRenderers = new List<SpriteRenderer>();
        private readonly Dictionary<int, Sprite> pedestalSprites = new Dictionary<int, Sprite>();
        private SpriteRenderer exitRenderer;

        private Sprite whiteSprite;

        private bool exitReady;

        private PlayerController player;

        private GameState state;

        private int collected;

        private float hazardCooldownUntil;

        private float mobileInputUntil;

        private float hintUntil;

        private string hint = "";

        private GUIStyle titleStyle;

        private GUIStyle textStyle;

        private GUIStyle centerStyle;

        private GUIStyle buttonStyle;

        private GUIStyle labelSmallStyle;

        private Texture2D panelTexture;

        private Texture2D buttonTexture;

        private Texture2D buttonPressedTexture;

        private Texture2D buttonActiveTexture;

        private GUIStyle padButtonActiveStyle;

        private Texture2D hintTexture;

        private Texture2D overlayTexture;

        private Texture2D labelBackdropTexture;

        // Таймер кулдауна: время, до которого повторный штраф на ЭТОЙ КОНКРЕТНОЙ копии игнорируется.
        // Ключ — itemIndex, значение — Time.time + 1.0f.
        private readonly Dictionary<int, float> fakeCooldowns = new Dictionary<int, float>();

        // Кэшированные спрайты мозаик (создаются один раз в BuildLevel).
        private Sprite authenticSprite;
        private Sprite fakeSprite;

        private void Awake()
        {
            Instance = this;

            Application.targetFrameRate = 60;

            BuildLevel();

            RestartGame();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.R))
            {
                RestartGame();
            }

            if (Input.GetKeyDown(KeyCode.F6))
            {
                DebugCollectOne();
            }

            if (Input.GetKeyDown(KeyCode.F7))
            {
                DebugSetState(true);
            }

            if (Input.GetKeyDown(KeyCode.F8))
            {
                DebugSetState(false);
            }

            if (Input.GetKeyDown(KeyCode.F9))
            {
                CaptureScreenshot();
            }

            if (state == GameState.Playing && Time.time >= mobileInputUntil)
            {
                MobileDirection = Vector2.zero;
            }

            // Пульсирующая подсветка зоны реставрации после сбора всех подлинников.
            if (exitReady && exitRenderer != null)
            {
                float t = 0.5f + 0.5f * Mathf.Sin(Time.time * 6f);
                Color glowA = new Color(1.00f, 1.35f, 1.05f);
                Color glowB = new Color(1.35f, 2.10f, 1.45f);
                exitRenderer.color = Color.Lerp(glowA, glowB, t);
            }
        }

        // ------------------------------------------------------------------
        // Генерация мира
        // ------------------------------------------------------------------

        private void BuildLevel()
        {
            CreateCamera();

            whiteSprite = CreateSprite(Shape.Square, Color.white);

            CreateDecor();
            CreateOuterWalls();
            CreateMuseumDecorations();
            CreatePlayer();
            CreateExit();

            authenticSprite = CreateSprite(Shape.AuthenticMosaic, Color.white);
            fakeSprite = CreateSprite(Shape.FakeMosaic, Color.white);
        }

        private void CreateCamera()
        {
            Camera cam = Camera.main;

            if (cam == null)
            {
                GameObject go = new GameObject("MainCamera");
                go.tag = "MainCamera";
                cam = go.AddComponent<Camera>();
                go.AddComponent<AudioListener>();
            }

            cam.orthographic = true;
            cam.orthographicSize = OrthoSize;
            cam.transform.position = new Vector3(0f, 0f, -10f);
            cam.transform.rotation = Quaternion.identity;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.03f, 0.03f, 0.05f);

            gameCamera = cam;
        }

        /// <summary>
        /// Пол зала 16.7 × 9.3 в (0, 0) закрывает всё пространство внутри внешних стен
        /// (WallLeft = -8.35, WallRight = 8.35, WallTop = 4.65, WallBottom = -4.65)
        /// без чёрных полос по краям. Плитка — приглушённый мрамор со швами.
        /// </summary>
        private void CreateDecor()
        {
            GameObject field = CreateSpriteObject("Field", Vector2.zero, new Vector2(16.7f, 9.3f), Color.white, -10, whiteSprite);
            floorRenderer = field.GetComponent<SpriteRenderer>();

            const float step = 0.8f;
            const float tileSize = 0.76f;
            const int columns = 21;
            const int rows = 12;

            for (int cx = 0; cx < columns; cx++)
            {
                float tx = -8.0f + cx * step;

                for (int cy = 0; cy < rows; cy++)
                {
                    float ty = -4.4f + cy * step;

                    // Плитки у самой кромки пола обрезаны внешней стеной — не создаём их.
                    if (tx + tileSize * 0.5f > 8.32f || tx - tileSize * 0.5f < -8.32f
                        || ty + tileSize * 0.5f > 4.62f || ty - tileSize * 0.5f < -4.62f)
                    {
                        continue;
                    }

                    GameObject tile = CreateSpriteObject(
                        "Tile",
                        new Vector2(tx, ty),
                        new Vector2(tileSize, tileSize),
                        Color.white,
                        -9,
                        whiteSprite);

                    tileRenderers.Add(tile.GetComponent<SpriteRenderer>());
                    tileShades.Add(Random.Range(-0.035f, 0.035f));
                }
            }
        }

        /// <summary>Статичный внешний периметр зала (создаётся один раз).</summary>
        private void CreateOuterWalls()
        {
            CreateOuterWall("WallTop", new Vector2(0.00f, 4.65f), new Vector2(17.00f, 0.30f));
            CreateOuterWall("WallBottom", new Vector2(0.00f, -4.65f), new Vector2(17.00f, 0.30f));
            CreateOuterWall("WallLeft", new Vector2(-8.35f, 0.00f), new Vector2(0.30f, 9.60f));
            CreateOuterWall("WallRight", new Vector2(8.35f, 0.00f), new Vector2(0.30f, 9.60f));
        }

        private void CreateOuterWall(string name, Vector2 position, Vector2 size)
        {
            GameObject wall = CreateSpriteObject(name, position, size, Color.white, 0, whiteSprite);
            outerWallRenderers.Add(wall.GetComponent<SpriteRenderer>());

            BoxCollider2D collider = wall.AddComponent<BoxCollider2D>();
            collider.isTrigger = false;
            collider.size = Vector2.one;

            GameObject trim = CreateTrim(name, position, size);
            trimRenderers.Add(trim.GetComponent<SpriteRenderer>());
        }

        /// <summary>
        /// Световой молдинг по длинной стороне витрины (декоративный, без коллайдера).
        /// Возвращает объект молдинга — он живёт и умирает вместе со своей витриной.
        /// </summary>
        private GameObject CreateTrim(string wallName, Vector2 position, Vector2 size)
        {
            const float trimThickness = 0.06f;

            if (size.x > size.y)
            {
                return CreateSpriteObject(
                    wallName + "_Trim",
                    new Vector2(position.x, position.y + size.y * 0.5f - trimThickness * 0.5f),
                    new Vector2(size.x, trimThickness),
                    Color.white,
                    1,
                    whiteSprite);
            }

            return CreateSpriteObject(
                wallName + "_Trim",
                new Vector2(position.x - size.x * 0.5f + trimThickness * 0.5f, position.y),
                new Vector2(trimThickness, size.y),
                Color.white,
                1,
                whiteSprite);
        }

        /// <summary>
        /// Пересоздаёт внутренние витрины зала по выбранной планировке.
        /// Старые объекты (стены и молдинги) уничтожаются, список innerWalls очищается.
        /// </summary>
        private void SpawnInnerWalls(int index)
        {
            ClearInnerWalls();

            layoutIndex = index;
            WallSpec[] specs = layouts[index];

            for (int i = 0; i < specs.Length; i++)
            {
                WallSpec spec = specs[i];

                GameObject wall = CreateSpriteObject(
                    spec.Name,
                    spec.Position,
                    spec.Size,
                    Color.white,
                    0,
                    whiteSprite);

                SpriteRenderer renderer = wall.GetComponent<SpriteRenderer>();
                renderer.color = theme.WallInner;

                BoxCollider2D collider = wall.AddComponent<BoxCollider2D>();
                collider.isTrigger = false;
                collider.size = Vector2.one;

                GameObject trim = CreateTrim(spec.Name, spec.Position, spec.Size);
                trim.GetComponent<SpriteRenderer>().color = theme.WallTrim;

                innerWalls.Add(wall);
                innerWalls.Add(trim);
            }
        }

        private void ClearInnerWalls()
        {
            for (int i = 0; i < innerWalls.Count; i++)
            {
                if (innerWalls[i] != null)
                {
                    Destroy(innerWalls[i]);
                }
            }

            innerWalls.Clear();
        }

        /// <summary>Угловые постаменты-подставки (без Collider2D), окрашиваются темой зала.</summary>
        private void CreateMuseumDecorations()
        {
            Vector2[] pedestalPositions =
            {
                new Vector2(-7.60f,  3.90f), // Верхний левый угол
                new Vector2( 7.60f,  3.90f), // Верхний правый угол (рядом с зоной реставрации)
                new Vector2(-7.60f, -3.90f), // Нижний левый угол
                new Vector2( 7.60f, -3.90f), // Нижний правый угол
                new Vector2(-7.70f,  0.90f), // Ниша левой стены
                new Vector2( 7.70f, -0.90f)  // Ниша правой стены
            };

            for (int i = 0; i < pedestalPositions.Length; i++)
            {
                GameObject pedestal = CreateSpriteObject(
                    "Pedestal_" + i,
                    pedestalPositions[i],
                    new Vector2(0.60f, 0.60f),
                    Color.white,
                    -5,
                    whiteSprite);

                pedestalRenderers.Add(pedestal.GetComponent<SpriteRenderer>());
            }
        }

        private void CreatePlayer()
        {
            Sprite playerSprite = CreateSprite(Shape.Circle, Color.white);
            Vector2 position = new Vector2(PlayerStartX, PlayerStartY);

            GameObject go = CreateSpriteObject(
                "Player",
                position,
                new Vector2(PlayerScale, PlayerScale),
                Color.white,
                6,
                playerSprite);

            Rigidbody2D body = go.AddComponent<Rigidbody2D>();
            body.gravityScale = 0f;
            body.freezeRotation = true;
            body.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            body.interpolation = RigidbodyInterpolation2D.Interpolate;

            CircleCollider2D collider = go.AddComponent<CircleCollider2D>();
            collider.radius = 0.44f;
            collider.isTrigger = false;
            player = go.AddComponent<PlayerController>();
        }

        private void CreateExit()
        {
            Sprite exitSprite = CreateSprite(Shape.Exit, Color.white);
            Vector2 position = new Vector2(ExitX, ExitY);

            GameObject go = CreateSpriteObject(
                "Exit",
                position,
                new Vector2(ExitScale, ExitScale),
                Color.white,
                2,
                exitSprite);

            exitRenderer = go.GetComponent<SpriteRenderer>();

            BoxCollider2D collider = go.AddComponent<BoxCollider2D>();
            collider.isTrigger = true;
            collider.size = Vector2.one;

            go.AddComponent<ExitZone>();
        }

        // ------------------------------------------------------------------
        // Темы зала
        // ------------------------------------------------------------------

        private int PickThemeIndex()
        {
            if (themes.Length <= 1)
            {
                return 0;
            }

            int next = Random.Range(0, themes.Length);

            // Гарантируем смену обстановки при рестарте.
            if (next == themeIndex)
            {
                next = (next + 1) % themes.Length;
            }

            return next;
        }

        private void ApplyTheme(int index)
        {
            themeIndex = index;
            theme = themes[index];

            if (gameCamera != null)
            {
                gameCamera.backgroundColor = theme.Ambient;
            }

            if (floorRenderer != null)
            {
                floorRenderer.color = theme.FloorBase;
            }

            for (int i = 0; i < tileRenderers.Count; i++)
            {
                tileRenderers[i].color = Shade(theme.FloorTile, tileShades[i]);
            }

            for (int i = 0; i < outerWallRenderers.Count; i++)
            {
                outerWallRenderers[i].color = theme.WallOuter;
            }

            for (int i = 0; i < trimRenderers.Count; i++)
            {
                trimRenderers[i].color = theme.WallTrim;
            }

            Sprite pedestalSprite = GetPedestalSprite(index);

            for (int i = 0; i < pedestalRenderers.Count; i++)
            {
                pedestalRenderers[i].sprite = pedestalSprite;
            }
        }

        private Sprite GetPedestalSprite(int index)
        {
            Sprite cached;

            if (pedestalSprites.TryGetValue(index, out cached))
            {
                return cached;
            }

            Sprite created = CreateSprite(Shape.Pedestal, themes[index].Pedestal);
            pedestalSprites[index] = created;

            return created;
        }

        private static Color Shade(Color color, float delta)
        {
            return new Color(
                Mathf.Clamp01(color.r + delta),
                Mathf.Clamp01(color.g + delta),
                Mathf.Clamp01(color.b + delta),
                color.a);
        }

        // ------------------------------------------------------------------
        // Безопасная рандомная раскладка мозаик
        // ------------------------------------------------------------------

        /// <summary>
        /// Формирует список точек, пригодных для спавна при текущей планировке:
        /// точка должна отстоять от границы каждой внутренней витрины
        /// не менее чем на ItemWallClearance.
        /// </summary>
        private void BuildValidSpawnPoints()
        {
            validSpawnPoints.Clear();

            for (int i = 0; i < spawnPool.Length; i++)
            {
                Vector2 point = spawnPool[i];

                if (Vector2.Distance(point, new Vector2(PlayerStartX, PlayerStartY)) < ItemAnchorClearance)
                {
                    continue;
                }

                if (Vector2.Distance(point, new Vector2(ExitX, ExitY)) < ItemAnchorClearance)
                {
                    continue;
                }

                bool fits = true;

                for (int w = 0; w < innerWalls.Count; w++)
                {
                    GameObject wall = innerWalls[w];

                    if (wall == null || wall.GetComponent<BoxCollider2D>() == null)
                    {
                        // Молдинги без коллайдера не учитываются.
                        continue;
                    }

                    if (DistanceToWallRect(point, wall.transform.position, wall.transform.localScale) < ItemWallClearance)
                    {
                        fits = false;
                        break;
                    }
                }

                if (fits)
                {
                    validSpawnPoints.Add(point);
                }
            }
        }

        /// <summary>
        /// Расстояние от точки до прямоугольника стены (0, если точка внутри).
        /// </summary>
        private static float DistanceToWallRect(Vector2 point, Vector2 center, Vector2 size)
        {
            float dx = Mathf.Max(Mathf.Abs(point.x - center.x) - size.x * 0.5f, 0f);
            float dy = Mathf.Max(Mathf.Abs(point.y - center.y) - size.y * 0.5f, 0f);

            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// Перемешивание Фишера-Йетса и создание 5 подлинников + 4 копий
        /// на позициях, свободных от текущих внутренних витрин.
        /// </summary>
        private void SpawnItems()
        {
            BuildValidSpawnPoints();

            shuffledSpawn.Clear();
            shuffledSpawn.AddRange(validSpawnPoints);

            // Аварийный резерв: после фильтрации должно остаться минимум 9 позиций.
            if (shuffledSpawn.Count < Target + MaxErrors + 1)
            {
                for (int i = 0; i < fallbackSpawn.Length && shuffledSpawn.Count < Target + MaxErrors + 1; i++)
                {
                    if (!shuffledSpawn.Contains(fallbackSpawn[i]))
                    {
                        shuffledSpawn.Add(fallbackSpawn[i]);
                    }
                }
            }

            // Алгоритм Фишера-Йетса (in-place).
            for (int i = shuffledSpawn.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                Vector2 tmp = shuffledSpawn[i];
                shuffledSpawn[i] = shuffledSpawn[j];
                shuffledSpawn[j] = tmp;
            }

            // Первые 5 позиций — подлинники (itemType = 0).
            for (int i = 0; i < Target; i++)
            {
                GameObject go = CreateSpriteObject(
                    "AuthenticMosaic",
                    shuffledSpawn[i],
                    new Vector2(ItemScale, ItemScale),
                    Color.white,
                    3,
                    authenticSprite);

                PolygonCollider2D collider = go.AddComponent<PolygonCollider2D>();
                collider.isTrigger = true;

                Collectible item = go.AddComponent<Collectible>();
                item.itemType = 0;
                item.itemIndex = i + 1;

                createdObjects.Add(go);
            }

            // Следующие 4 позиции — копии (itemType = 1).
            for (int i = 0; i < MaxErrors + 1; i++)
            {
                GameObject go = CreateSpriteObject(
                    "FakeMosaic",
                    shuffledSpawn[Target + i],
                    new Vector2(ItemScale, ItemScale),
                    Color.white,
                    3,
                    fakeSprite);

                PolygonCollider2D collider = go.AddComponent<PolygonCollider2D>();
                collider.isTrigger = true;

                Collectible item = go.AddComponent<Collectible>();
                item.itemType = 1;
                item.itemIndex = 6 + i;

                createdObjects.Add(go);
            }
        }

        private GameObject CreateSpriteObject(string name, Vector2 pos, Vector2 scale, Color col, int order, Sprite sprite)
        {
            GameObject go = new GameObject(name);
            go.transform.position = new Vector3(pos.x, pos.y, 0f);
            go.transform.localScale = new Vector3(scale.x, scale.y, 1f);

            SpriteRenderer renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = sprite;
            renderer.color = col;
            renderer.sortingOrder = order;

            return go;
        }

        // ------------------------------------------------------------------
        // Процедурная графика: текстуры 128×128
        // ------------------------------------------------------------------

        /// <summary>
        /// Генерация спрайта 128×128 по математической формуле формы.
        /// Пиксель принадлежит фигуре, если выполняются условия для его
        /// нормализованных координат nx, ny в диапазоне [-1, 1].
        /// </summary>
        private static Sprite CreateSprite(Shape shape, Color color)
        {
            const int size = 128;

            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            Color clear = new Color(0f, 0f, 0f, 0f);

            // Робот-реставратор.
            Color hullDark = new Color(0.07f, 0.11f, 0.28f);
            Color hullMid = new Color(0.12f, 0.19f, 0.42f);
            Color hullLine = new Color(0.05f, 0.08f, 0.20f);
            Color neonRim = new Color(0.18f, 0.88f, 1.00f);
            Color lensRing = new Color(0.06f, 0.10f, 0.22f);
            Color lensGlow = new Color(0.35f, 0.95f, 1.00f);
            Color lensCore = new Color(0.95f, 1.00f, 1.00f);
            Color gold = new Color(1.00f, 0.82f, 0.24f);
            Color goldLight = new Color(1.00f, 0.93f, 0.55f);
            Color goldDeep = new Color(0.78f, 0.56f, 0.10f);

            // Зона реставрации.
            Color emerald = new Color(0.18f, 0.62f, 0.36f);
            Color emeraldLight = new Color(0.30f, 0.90f, 0.55f);
            Color emeraldDeep = new Color(0.10f, 0.42f, 0.26f);

            // Подлинный фрагмент — золотой медальон.
            Color sapphire = new Color(0.10f, 0.32f, 0.72f);
            Color turquoise = new Color(0.16f, 0.78f, 0.86f);
            Color mosaicSeam = new Color(0.06f, 0.18f, 0.46f);
            Color whiteGlint = new Color(1.00f, 1.00f, 1.00f);
            Color medallionDark = new Color(0.30f, 0.20f, 0.05f);

            // Поддельная копия.
            Color burgundy = new Color(0.26f, 0.06f, 0.11f);
            Color burgundyLight = new Color(0.40f, 0.10f, 0.16f);
            Color brokenFrame = new Color(0.62f, 0.34f, 0.20f);
            Color scarlet = new Color(0.96f, 0.16f, 0.20f);

            Color[] pixels = new Color[size * size];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float nx = x / (size - 1f) * 2f - 1f;
                    float ny = y / (size - 1f) * 2f - 1f;
                    float ax = Mathf.Abs(nx);
                    float ay = Mathf.Abs(ny);
                    float r2 = nx * nx + ny * ny;
                    Color pixel = clear;

                    switch (shape)
                    {
                        case Shape.Square:
                            pixel = color;
                            break;

                        case Shape.Circle:
                        {
                            // Тёмно-синий бронекорпус.
                            if (r2 <= 0.92f)
                            {
                                if (r2 > 0.80f)
                                {
                                    pixel = neonRim;
                                }
                                else
                                {
                                    float t = r2 / 0.80f;
                                    pixel = Color.Lerp(hullMid, hullDark, t);

                                    // Панельные линии брони.
                                    if (Mathf.Abs(ny - 0.42f) < 0.035f && ax < 0.55f)
                                    {
                                        pixel = hullLine;
                                    }

                                    if (Mathf.Abs(ny + 0.46f) < 0.035f && ax < 0.48f)
                                    {
                                        pixel = hullLine;
                                    }
                                }
                            }

                            // Светящаяся оптическая линза по центру.
                            float lens = Mathf.Sqrt(r2);

                            if (lens < 0.40f && lens >= 0.32f)
                            {
                                pixel = lensRing;
                            }

                            if (lens < 0.32f)
                            {
                                pixel = lens < 0.12f ? lensCore : lensGlow;
                            }

                            // Два золотых манипулятора по бокам.
                            if (ax >= 0.86f && ay <= 0.12f)
                            {
                                pixel = ax > 0.96f ? goldLight : gold;
                            }

                            break;
                        }

                        case Shape.Exit:
                        {
                            // Изумрудный монтажный стол с золотой окантовкой и прицелом.
                            float m = Mathf.Max(ax, ay);

                            if (m <= 0.94f)
                            {
                                if (m >= 0.86f)
                                {
                                    pixel = gold;
                                }
                                else if (m >= 0.80f)
                                {
                                    pixel = emeraldDeep;
                                }
                                else
                                {
                                    float t = m / 0.80f;
                                    pixel = Color.Lerp(emeraldLight, emerald, t);
                                }

                                // Внутренняя золотая рамка-прицел.
                                if (Mathf.Abs(m - 0.56f) < 0.022f)
                                {
                                    pixel = gold;
                                }

                                // Внутренний прицел-крест.
                                if ((ax < 0.035f && ay < 0.52f) || (ay < 0.035f && ax < 0.52f))
                                {
                                    pixel = emeraldLight;
                                }

                                if (r2 < 0.012f)
                                {
                                    pixel = lensCore;
                                }
                            }

                            break;
                        }

                        case Shape.Diamond:
                        case Shape.Cross:
                            if (ax + ay <= 0.92f)
                            {
                                pixel = color;
                            }

                            break;

                        case Shape.AuthenticMosaic:
                        {
                            // Золотой медальон-подлинник: целая яркая рамка + сапфирово-бирюзовый узор.
                            float d = ax + ay;

                            if (d <= 0.94f)
                            {
                                if (d >= 0.74f)
                                {
                                    // Целая непрерывная рамка в три тона.
                                    pixel = d >= 0.90f ? goldLight : (d >= 0.82f ? gold : goldDeep);
                                }
                                else
                                {
                                    float t = d / 0.74f;
                                    pixel = Color.Lerp(turquoise, sapphire, t);

                                    // Мозаичные швы (смальта).
                                    const float cell = 0.26f;
                                    bool seam = Mathf.Repeat(nx + 1f, cell) < 0.035f
                                                || Mathf.Repeat(ny + 1f, cell) < 0.035f;

                                    if (seam)
                                    {
                                        pixel = mosaicSeam;
                                    }

                                    // Круглая сердцевина медальона.
                                    if (r2 < 0.10f)
                                    {
                                        pixel = r2 < 0.04f ? goldLight : gold;
                                    }
                                }

                                // Белые блики у четырёх вершин ромба.
                                if (NearPoint(nx, ny, 0.71f, 0f, 0.075f)
                                    || NearPoint(nx, ny, -0.71f, 0f, 0.075f)
                                    || NearPoint(nx, ny, 0f, 0.71f, 0.075f)
                                    || NearPoint(nx, ny, 0f, -0.71f, 0.075f))
                                {
                                    pixel = whiteGlint;
                                }
                            }

                            break;
                        }

                        case Shape.FakeMosaic:
                        {
                            // Тёмно-бордовый восьмиугольный щит со сломанной рамкой и алым X.
                            bool insideOctagon = ax <= 0.90f && ay <= 0.90f && (ax + ay) <= 1.42f;

                            if (insideOctagon)
                            {
                                float m = Mathf.Max(ax, ay);
                                float ch = ax + ay;

                                pixel = Color.Lerp(burgundyLight, burgundy, Mathf.Clamp01(m / 0.90f));

                                // Сломанная (разорванная) рамка по периметру.
                                if ((ax > 0.76f || ay > 0.76f) && ch < 1.42f)
                                {
                                    float gapNoise = Mathf.Sin(nx * 11.0f + 1.3f) * Mathf.Cos(ny * 9.0f);
                                    pixel = gapNoise > 0.42f ? burgundy : brokenFrame;
                                }

                                // Яркий алый крест X по центру.
                                if (Mathf.Abs(nx - ny) < 0.085f || Mathf.Abs(nx + ny) < 0.085f)
                                {
                                    if (m < 0.60f)
                                    {
                                        pixel = scarlet;
                                    }
                                }
                            }

                            break;
                        }

                        case Shape.Pedestal:
                        {
                            // Музейный постамент: тумба, верхняя плита, золотая кайма, тёмный контур.
                            if (Mathf.Max(ax, ay) <= 0.88f)
                            {
                                Color top = new Color(
                                    Mathf.Clamp01(color.r * 1.6f + 0.10f),
                                    Mathf.Clamp01(color.g * 1.6f + 0.10f),
                                    Mathf.Clamp01(color.b * 1.6f + 0.10f));

                                pixel = color;

                                if (ay > 0.38f)
                                {
                                    pixel = top;
                                }

                                if (Mathf.Abs(ny - 0.40f) < 0.045f)
                                {
                                    pixel = gold;
                                }

                                if (ax > 0.84f || ay > 0.84f)
                                {
                                    pixel = new Color(0.05f, 0.04f, 0.04f);
                                }
                            }

                            break;
                        }
                    }

                    pixels[y * size + x] = pixel;
                }
            }

            texture.SetPixels(pixels);
            texture.Apply(false, false);

            return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), PixelPerUnit);
        }

        private static bool NearPoint(float nx, float ny, float px, float py, float radius)
        {
            float dx = nx - px;
            float dy = ny - py;

            return dx * dx + dy * dy <= radius * radius;
        }

        /// <summary>Одноцветная 1×1 текстура для GUI-панелей.</summary>
        private static Texture2D SolidTexture(Color color)
        {
            Texture2D texture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };

            texture.SetPixel(0, 0, color);
            texture.Apply(false, false);

            return texture;
        }

        // ------------------------------------------------------------------
        // Игровая логика
        // ------------------------------------------------------------------

        /// <summary>Контакт робота с фрагментом мозаики.</summary>
        public void Collect(Collectible item)
        {
            if (!IsPlaying || item == null || !createdObjects.Contains(item.gameObject))
            {
                return;
            }

            if (item.itemType == 0)
            {
                // Подлинник: мгновенное удаление из реестра и сцены (защита от повторного зачёта).
                createdObjects.Remove(item.gameObject);
                Destroy(item.gameObject);

                selection.Take(true);
                collected = selection.Progress;

                if (collected >= Target)
                {
                    // Уровень НЕ завершается: подлинники нужно доставить в зону реставрации.
                    HighlightExit();
                    ShowHint("Все 5 подлинников собраны! Доставьте их в зону реставрации (зелёный стол)", 5.0f);
                }
                else
                {
                    ShowHint("Подлинный фрагмент найден: " + collected + "/" + Target);
                }
            }
            else if (item.itemType == 1)
            {
                // Копия: индивидуальный кулдаун на повторный штраф (1.0 с).
                if (fakeCooldowns.TryGetValue(item.itemIndex, out float until) && Time.time < until)
                {
                    return;
                }

                selection.Take(false);
                fakeCooldowns[item.itemIndex] = Time.time + 1.0f;

                ShowHint("Ошибка реставратора: обнаружена копия! Ошибок: " + selection.Errors + "/" + MaxErrors);

                if (selection.Errors >= MaxErrors)
                {
                    state = GameState.Lost;
                    MobileDirection = Vector2.zero;
                    hintUntil = 0f;
                }
            }
        }

        /// <summary>Ярко-изумрудная подсветка зоны реставрации после сбора комплекта.</summary>
        private void HighlightExit()
        {
            exitReady = true;

            if (exitRenderer != null)
            {
                exitRenderer.color = new Color(1.15f, 1.70f, 1.25f);
            }
        }

        /// <summary>Штрафное столкновение с сигнализацией зала.</summary>
        public void HitHazard()
        {
            if (!IsPlaying || Time.time < hazardCooldownUntil)
            {
                return;
            }

            hazardCooldownUntil = Time.time + 1.0f;
            ShowHint("Сигнализация зала! Робот возвращён на старт.");

            if (player != null)
            {
                player.ReturnToStart();
            }
        }

        /// <summary>
        /// Завершение уровня по входу в зону реставрации (ExitZone).
        /// Победа возможна только при полном комплекте подлинников.
        /// </summary>
        public void TryFinishLevel()
        {
            if (!IsPlaying)
            {
                return;
            }

            if (selection.Progress >= Target)
            {
                state = GameState.Won;
                MobileDirection = Vector2.zero;

                // Гасим остаточную скорость тела: робот замирает в зоне реставрации.
                if (player != null)
                {
                    player.Stop();
                }

                hintUntil = 0f;
                // Дальнейшее движение блокируется: IsPlaying == false → FixedUpdate обнуляет linearVelocity.
            }
            else
            {
                ShowHint("Зона реставрации закрыта: собрано " + selection.Progress + " из " + Target + " подлинников");
            }
        }

        /// <summary>
        /// Полный сброс уровня: новая планировка внутренних витрин, новая тема зала,
        /// новая раскладка мозаик с учётом текущих преград.
        /// </summary>
        public void RestartGame()
        {
            // Уничтожаем все мозаики предыдущего раунда.
            for (int i = 0; i < createdObjects.Count; i++)
            {
                if (createdObjects[i] != null)
                {
                    Destroy(createdObjects[i]);
                }
            }

            createdObjects.Clear();
            fakeCooldowns.Clear();

            selection.Reset();
            collected = 0;

            hazardCooldownUntil = 0f;
            mobileInputUntil = 0f;
            hintUntil = 0f;
            hint = "";
            MobileDirection = Vector2.zero;
            state = GameState.Playing;

            // Возвращаем зону реставрации в исходное состояние ожидания.
            exitReady = false;

            if (exitRenderer != null)
            {
                exitRenderer.color = Color.white;
            }

            // Новая цветовая тема музея на каждый запуск/рестарт.
            ApplyTheme(PickThemeIndex());

            // Новая случайная планировка внутренних витрин (4 проверенных варианта).
            SpawnInnerWalls(Random.Range(0, layouts.Length));

            if (player != null)
            {
                player.ReturnToStart();
            }

            // Мозаики раскладываются только по точкам, свободным от текущих витрин.
            SpawnItems();
        }

        private void ShowHint(string message)
        {
            ShowHint(message, 2.4f);
        }

        private void ShowHint(string message, float duration)
        {
            hint = message;
            hintUntil = Time.time + duration;
        }

        private Collectible FindCollectible(int itemType)
        {
            for (int i = 0; i < createdObjects.Count; i++)
            {
                GameObject go = createdObjects[i];
                if (go == null)
                {
                    continue;
                }

                Collectible item = go.GetComponent<Collectible>();
                if (item != null && item.itemType == itemType)
                {
                    return item;
                }
            }

            return null;
        }

        /// <summary>F6: служебный подбор одного подлинника.</summary>
        public void DebugCollectOne()
        {
            if (!IsPlaying)
            {
                return;
            }

            Collectible item = FindCollectible(0);
            if (item != null)
            {
                Collect(item);
            }
            else
            {
                ShowHint("Подлинники закончились или игра завершена.");
            }
        }

        /// <summary>F7 / F8: принудительная установка состояния победы или поражения.</summary>
        public void DebugSetState(bool won)
        {
            if (!IsPlaying)
            {
                return;
            }

            state = won ? GameState.Won : GameState.Lost;
            MobileDirection = Vector2.zero;
            hintUntil = 0f;

            // Для корректного отчёта F7 принудительно доводит регистры до целевых значений.
            if (won)
            {
                while (selection.Progress < Target)
                {
                    selection.Take(true);
                }

                HighlightExit();
            }
            else
            {
                while (selection.Errors < MaxErrors)
                {
                    selection.Take(false);
                }
            }

            if (player != null)
            {
                player.ReturnToStart();
            }
        }

        /// <summary>F9: сохранение снимка экрана в Documentation/Screenshots/ (корень проекта).</summary>
        public void CaptureScreenshot()
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string folder = Path.Combine(projectRoot, "Documentation", "Screenshots");
            Directory.CreateDirectory(folder);

            string fileName = "screenshot_" + System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".png";
            string path = Path.Combine(folder, fileName);

            ScreenCapture.CaptureScreenshot(path);
            ShowHint("Снимок сохранён: Documentation/Screenshots/" + fileName);
        }

        // ------------------------------------------------------------------
        // Интерфейс OnGUI (музейный тон)
        // ------------------------------------------------------------------

        private void EnsureGuiResources()
        {
            if (panelTexture != null)
            {
                return;
            }

            panelTexture = SolidTexture(new Color(0.13f, 0.09f, 0.07f, 0.96f));
            buttonTexture = SolidTexture(new Color(0.22f, 0.14f, 0.08f, 0.95f));
            buttonPressedTexture = SolidTexture(new Color(0.38f, 0.24f, 0.12f, 1f));
            // Плотная бирюзовая заливка активной кнопки D-Pad (не прозрачная, чтобы подсветка читалась).
            buttonActiveTexture = SolidTexture(new Color(0.25f, 0.85f, 0.95f, 1f));
            hintTexture = SolidTexture(new Color(0.10f, 0.07f, 0.05f, 0.94f));
            overlayTexture = SolidTexture(new Color(0.02f, 0.01f, 0.01f, 0.78f));
            labelBackdropTexture = SolidTexture(new Color(0f, 0f, 0f, 0.75f));
        }

        private void OnGUI()
        {
            EnsureGuiResources();

            float scale = Mathf.Min(Screen.width / (float)ReferenceWidth, Screen.height / (float)ReferenceHeight);
            if (scale <= 0f)
            {
                return;
            }

            Matrix4x4 saved = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(Vector3.one * scale);

            if (titleStyle == null)
            {
                BuildStyles();
            }

            DrawTopPanel();
            DrawWorldLabels();
            DrawMobilePad();
            DrawHintsPanel();

            if (Time.time < hintUntil && !string.IsNullOrEmpty(hint))
            {
                GUI.DrawTexture(new Rect(330, 596, 620, 56), hintTexture);
                GUI.Label(new Rect(340, 596, 600, 56), hint, centerStyle);
            }

            if (state != GameState.Playing)
            {
                DrawResultModal();
            }

            GUI.matrix = saved;
        }

        private void BuildStyles()
        {
            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 28,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft
            };
            titleStyle.normal.textColor = new Color(1f, 0.86f, 0.55f);

            textStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 22,
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.MiddleCenter
            };
            textStyle.normal.textColor = Color.white;

            centerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 19,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true
            };
            centerStyle.normal.textColor = Color.white;

            buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 24,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            buttonStyle.normal.background = buttonTexture;
            buttonStyle.hover.background = buttonPressedTexture;
            buttonStyle.active.background = buttonPressedTexture;
            buttonStyle.normal.textColor = Color.white;
            buttonStyle.hover.textColor = Color.white;
            buttonStyle.active.textColor = Color.white;

            labelSmallStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };

            // Активная (удерживаемая) кнопка D-Pad: бирюзовая подложка вместо коричневой.
            padButtonActiveStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 24,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            padButtonActiveStyle.normal.background = buttonActiveTexture;
            padButtonActiveStyle.hover.background = buttonActiveTexture;
            padButtonActiveStyle.active.background = buttonActiveTexture;
            padButtonActiveStyle.normal.textColor = Color.white;
            padButtonActiveStyle.hover.textColor = Color.white;
            padButtonActiveStyle.active.textColor = Color.white;
        }

        private void DrawTopPanel()
        {
            GUI.DrawTexture(new Rect(24, 20, 1232, 78), panelTexture);

            GUI.Label(new Rect(44, 20, 400, 78), "МУЗЕЙНЫЙ РЕСТАВРАТОР", titleStyle);

            GUIStyle progressStyle = new GUIStyle(textStyle);
            progressStyle.fontStyle = FontStyle.Bold;
            progressStyle.normal.textColor = new Color(1f, 0.86f, 0.35f);
            GUI.Label(new Rect(444, 20, 220, 78), "Подлинники: " + selection.Progress + "/" + Target, progressStyle);

            GUIStyle errorStyle = new GUIStyle(textStyle);
            errorStyle.fontStyle = FontStyle.Bold;
            errorStyle.normal.textColor = selection.Errors >= 2 ? new Color(1f, 0.40f, 0.32f) : new Color(0.70f, 0.95f, 0.72f);
            GUI.Label(new Rect(664, 20, 190, 78), "Ошибки: " + selection.Errors + "/" + MaxErrors, errorStyle);

            GUIStyle layoutStyle = new GUIStyle(labelSmallStyle);
            layoutStyle.fontSize = 15;
            layoutStyle.normal.textColor = new Color(0.80f, 0.86f, 0.95f);
            string layoutName = layoutIndex >= 0 && layoutIndex < layoutNames.Length ? layoutNames[layoutIndex] : "—";
            GUI.Label(new Rect(864, 20, 256, 78), "Зал: «" + layoutName + "»", layoutStyle);

            GUIStyle restartStyle = new GUIStyle(textStyle);
            restartStyle.fontSize = 18;
            restartStyle.normal.textColor = new Color(0.75f, 0.85f, 0.95f);
            GUI.Label(new Rect(1128, 20, 110, 78), "R — заново", restartStyle);
        }

        /// <summary>
        /// Подписи под объектами в мире: золотой «ПОДЛИННИК» для itemType == 0
        /// и алый «КОПИЯ» для itemType == 1. Позиция — строго через масштаб GUI-матрицы.
        /// </summary>
        private void DrawWorldLabels()
        {
            Camera cam = Camera.main;

            if (cam == null || labelBackdropTexture == null || labelSmallStyle == null)
            {
                return;
            }

            float scale = Mathf.Min(Screen.width / 1280f, Screen.height / 720f);

            if (scale <= 0f)
            {
                return;
            }

            for (int i = 0; i < createdObjects.Count; i++)
            {
                GameObject go = createdObjects[i];

                if (go == null)
                {
                    continue;
                }

                Collectible item = go.GetComponent<Collectible>();

                if (item == null)
                {
                    continue;
                }

                Vector3 screenPos = cam.WorldToScreenPoint(item.transform.position);

                if (screenPos.z < 0f)
                {
                    continue;
                }

                float guiX = screenPos.x / scale;
                float guiY = (Screen.height - screenPos.y) / scale;

                bool isAuthentic = item.itemType == 0;
                string text = isAuthentic ? "ПОДЛИННИК" : "КОПИЯ";

                GUIStyle style = new GUIStyle(labelSmallStyle);
                style.fontSize = 14;
                style.fontStyle = FontStyle.Bold;
                style.alignment = TextAnchor.MiddleCenter;
                style.normal.textColor = isAuthentic
                    ? new Color(1.00f, 0.86f, 0.35f)
                    : new Color(1.00f, 0.55f, 0.50f);

                Rect rect = new Rect(guiX - 58f, guiY - 44f, 116f, 22f);

                GUI.DrawTexture(rect, labelBackdropTexture);
                GUI.Label(rect, text, style);
            }
        }

        private void DrawMobilePad()
        {
            DrawPadButton(new Rect(110, 540, 72, 72), "↑", Vector2.up);
            DrawPadButton(new Rect(110, 620, 72, 72), "↓", Vector2.down);
            DrawPadButton(new Rect(30, 620, 72, 72), "←", Vector2.left);
            DrawPadButton(new Rect(190, 620, 72, 72), "→", Vector2.right);
        }

        /// <summary>
        /// Одна кнопка экранного D-Pad. Пока направление удерживается (экранная кнопка
        /// или соответствующая клавиша клавиатуры), кнопка подсвечивается бирюзовым.
        /// </summary>
        private void DrawPadButton(Rect rect, string label, Vector2 direction)
        {
            if (!IsPlaying)
            {
                return;
            }

            bool active = IsDirectionActive(direction);

            // Временная подмена фона: бирюзовая заливка для активной кнопки.
            GUI.backgroundColor = active ? new Color(0.25f, 0.85f, 0.95f, 1f) : Color.white;

            bool pressed = GUI.RepeatButton(rect, label, active ? padButtonActiveStyle : buttonStyle);

            GUI.backgroundColor = Color.white;

            if (pressed)
            {
                MobileDirection = direction;
                mobileInputUntil = Time.time + 0.08f;
            }
        }

        /// <summary>Проверяет удержание одного конкретного направления.</summary>
        private bool IsDirectionActive(Vector2 direction)
        {
            if (MobileDirection.sqrMagnitude > 0.0001f
                && Vector2.Dot(MobileDirection.normalized, direction) > 0.5f)
            {
                return true;
            }

            if (direction == Vector2.up)
            {
                return Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow);
            }

            if (direction == Vector2.down)
            {
                return Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow);
            }

            if (direction == Vector2.left)
            {
                return Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow);
            }

            return Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow);
        }

        /// <summary>Человекочитаемое описание текущего направления движения.</summary>
        private string DescribeDirection()
        {
            int x = 0;
            int y = 0;

            if (MobileDirection.sqrMagnitude > 0.0001f)
            {
                x = Mathf.Abs(MobileDirection.x) > 0.01f ? (int)Mathf.Sign(MobileDirection.x) : 0;
                y = Mathf.Abs(MobileDirection.y) > 0.01f ? (int)Mathf.Sign(MobileDirection.y) : 0;
            }
            else
            {
                if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow))
                {
                    x -= 1;
                }

                if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow))
                {
                    x += 1;
                }

                if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow))
                {
                    y -= 1;
                }

                if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow))
                {
                    y += 1;
                }
            }

            if (x == 0 && y == 0)
            {
                return "ожидание";
            }

            string vertical = y > 0 ? "↑" : (y < 0 ? "↓" : "");
            string horizontal = x < 0 ? "←" : (x > 0 ? "→" : "");

            if (vertical.Length > 0 && horizontal.Length > 0)
            {
                return vertical + horizontal;
            }

            return vertical.Length > 0 ? vertical : horizontal;
        }

        /// <summary>Нижняя плашка подсказок: сдвинута правее, чтобы не перекрывать кнопку «→».</summary>
        private void DrawHintsPanel()
        {
            GUI.DrawTexture(new Rect(290, 666, 965, 40), hintTexture);

            GUIStyle style = new GUIStyle(labelSmallStyle);
            style.fontSize = 14;
            style.alignment = TextAnchor.MiddleLeft;
            style.normal.textColor = new Color(0.78f, 0.86f, 0.94f);

            string layoutName = layoutIndex >= 0 && layoutIndex < layoutNames.Length ? layoutNames[layoutIndex] : "—";

            GUI.Label(
                new Rect(304, 666, 640, 40),
                "Зал: «" + layoutName + "» / «" + theme.Name + "»  ·  Соберите 5 подлинников и отнесите их в зелёный стол  ·  Копии = штраф  ·  R — заново",
                style);

            // Индикатор направления движения (экранный D-Pad или клавиатура).
            string directionText = DescribeDirection();
            bool moving = directionText != "ожидание";

            GUIStyle directionStyle = new GUIStyle(labelSmallStyle);
            directionStyle.fontSize = 15;
            directionStyle.fontStyle = FontStyle.Bold;
            directionStyle.alignment = TextAnchor.MiddleCenter;
            directionStyle.normal.textColor = moving
                ? new Color(0.35f, 0.92f, 1.00f)
                : new Color(0.52f, 0.58f, 0.64f);

            GUI.Label(new Rect(966, 666, 282, 40), "Движение: " + directionText, directionStyle);
        }

        private void DrawResultModal()
        {
            GUI.DrawTexture(new Rect(0, 0, ReferenceWidth, ReferenceHeight), overlayTexture);
            GUI.DrawTexture(new Rect(330, 214, 620, 336), panelTexture);

            bool won = state == GameState.Won;

            GUIStyle header = new GUIStyle(titleStyle);
            header.alignment = TextAnchor.MiddleCenter;
            header.fontSize = 40;
            header.normal.textColor = won ? new Color(0.55f, 1f, 0.65f) : new Color(1f, 0.50f, 0.42f);

            GUI.Label(new Rect(330, 236, 620, 60), won ? "УРОВЕНЬ ПРОЙДЕН" : "ПОПРОБУЙТЕ ЕЩЁ", header);

            GUIStyle body = new GUIStyle(centerStyle);
            body.fontSize = 21;
            body.normal.textColor = Color.white;

            string message = won
                ? "Все 5 подлинников доставлены на реставрацию!\nЭкспозиция восстановлена."
                : "Превышен лимит ошибок анализа.\nСлишком много копий принято за подлинники.";

            GUI.Label(new Rect(370, 306, 540, 64), message, body);

            GUIStyle stats = new GUIStyle(centerStyle);
            stats.fontSize = 18;
            stats.normal.textColor = new Color(0.80f, 0.88f, 0.96f);
            GUI.Label(
                new Rect(370, 384, 540, 30),
                "Подлинники: " + selection.Progress + "/" + Target + "    Ошибки: " + selection.Errors + "/" + MaxErrors,
                stats);

            if (GUI.Button(new Rect(475, 440, 330, 62), "НАЧАТЬ ЗАНОВО", buttonStyle))
            {
                RestartGame();
            }
        }
    }
}