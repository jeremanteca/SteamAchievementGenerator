using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO; // Para manejo de archivos y directorios
using HtmlAgilityPack; // Para parsear HTML
using Newtonsoft.Json; // Para JSON
using System.Net.Http; // Para descargar imágenes

namespace SteamAchievementGenerator
{
    public partial class Form1 : Form
    {
        private string _selectedHtmlFilePath;
        private HtmlAgilityPack.HtmlDocument _htmlDoc;
        private List<Achievement> _parsedAchievements;
        private GameInfo _parsedGameInfo;

        // Clases para almacenar la información
        public class GameInfo
        {
            public string Name { get; set; }
            public string AppId { get; set; }
            public string Developer { get; set; }
            public string ReleaseDate { get; set; }
            public string HeaderImagePath { get; set; }
            public int AchievementCount { get; set; }
        }

        public class Achievement
        {
            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("displayName")]
            public Dictionary<string, string> DisplayName { get; set; }

            [JsonProperty("description")]
            public Dictionary<string, string> Description { get; set; }

            [JsonProperty("hidden")]
            public string Hidden { get; set; } // "0" o "1"

            [JsonProperty("icon")]
            public string Icon { get; set; }

            [JsonProperty("icon_gray")]
            public string IconGray { get; set; }
        }


        public Form1()
        {
            InitializeComponent();
            _parsedAchievements = new List<Achievement>();
            _parsedGameInfo = new GameInfo();
        }

        private async void btnSelectHtml_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = "HTML Files (*.html;*.htm)|*.html;*.htm|All files (*.*)|*.*";
                openFileDialog.RestoreDirectory = true;

                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    _selectedHtmlFilePath = openFileDialog.FileName;
                    txtHtmlPath.Text = _selectedHtmlFilePath;
                    lblStatus.Text = "Loading HTML...";
                    Application.DoEvents(); // Para refrescar la UI

                    try
                    {
                        _htmlDoc = new HtmlAgilityPack.HtmlDocument();
                        // Leer el archivo con la codificación correcta (UTF-8 es común para HTML)
                        _htmlDoc.Load(_selectedHtmlFilePath, Encoding.UTF8);

                        ParseGameInfo();
                        UpdateGameInfoUI();

                        ParseAchievementsList(); // Parsear pero no descargar imágenes aún
                        lblAchievementsCountValue.Text = _parsedAchievements.Count.ToString();

                        btnGenerate.Enabled = true;
                        lblStatus.Text = "HTML loaded. Ready for generation.";
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error loading HTML: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        lblStatus.Text = "Error loading HTML.";
                        btnGenerate.Enabled = false;
                    }
                }
            }
        }

        private void ParseGameInfo()
        {
            if (_htmlDoc == null) return;
            _parsedGameInfo = new GameInfo();

            // Nombre del Juego (intentar desde H1 y luego desde Title)
            var gameNameNode = _htmlDoc.DocumentNode.SelectSingleNode("//div[@class='pagehead-title']//h1[@itemprop='name']");
            if (gameNameNode != null)
            {
                _parsedGameInfo.Name = HtmlEntity.DeEntitize(gameNameNode.InnerText.Trim());
            }
            else
            {
                gameNameNode = _htmlDoc.DocumentNode.SelectSingleNode("//title");
                if (gameNameNode != null)
                {
                    _parsedGameInfo.Name = HtmlEntity.DeEntitize(gameNameNode.InnerText.Trim()).Replace("· SteamDB", "").Trim();
                }
            }


            // App ID
            var appIdNode = _htmlDoc.DocumentNode.SelectSingleNode("//div[contains(@class, 'scope-app')]");
            if (appIdNode != null)
            {
                _parsedGameInfo.AppId = appIdNode.GetAttributeValue("data-appid", "(No encontrado)");
            }

            // Developer
            var developerNode = _htmlDoc.DocumentNode.SelectSingleNode("//table[contains(@class,'table-responsive-flex')]//tr[td[1]='Developer']/td[2]/a");
            if (developerNode == null) // Intento alternativo si la estructura es ligeramente diferente
            {
                developerNode = _htmlDoc.DocumentNode.SelectSingleNode("//table[contains(@class,'table-responsive-flex')]//tr[td[contains(., 'Developer')]]/td[2]/a");
            }
            _parsedGameInfo.Developer = developerNode != null ? HtmlEntity.DeEntitize(developerNode.InnerText.Trim()) : "(No encontrado)";


            // Release Date
            var releaseDateNode = _htmlDoc.DocumentNode.SelectSingleNode("//table[contains(@class,'table-responsive-flex')]//tr[td[1]='Release Date']/td[2]");
            if (releaseDateNode == null) // Intento alternativo
            {
                releaseDateNode = _htmlDoc.DocumentNode.SelectSingleNode("//table[contains(@class,'table-responsive-flex')]//tr[td[contains(., 'Release Date')]]/td[2]");
            }
            if (releaseDateNode != null)
            {
                // El nodo puede contener un <i> con la fecha relativa, queremos el texto principal.
                var timeNode = releaseDateNode.SelectSingleNode("./i/relative-time");
                if (timeNode != null)
                {
                    _parsedGameInfo.ReleaseDate = timeNode.GetAttributeValue("datetime", releaseDateNode.InnerText.Trim());
                    // Intentar formatear la fecha a algo más legible si es un datetime
                    if (DateTime.TryParse(_parsedGameInfo.ReleaseDate, out DateTime parsedDate))
                    {
                        _parsedGameInfo.ReleaseDate = parsedDate.ToString("dd MMMM yyyy");
                    }
                }
                else
                {
                    // Si no hay relative-time, tomar el texto y limpiar
                    var fullText = HtmlEntity.DeEntitize(releaseDateNode.InnerText.Trim());
                    // Remover el texto del tooltip si existe (ej. (2 days ago))
                    int parenthesisIndex = fullText.IndexOf(" (");
                    if (parenthesisIndex > 0)
                    {
                        _parsedGameInfo.ReleaseDate = fullText.Substring(0, parenthesisIndex).Trim();
                    }
                    else
                    {
                        _parsedGameInfo.ReleaseDate = fullText;
                    }
                }
            }
            else
            {
                _parsedGameInfo.ReleaseDate = "(No encontrado)";
            }


            // Header Image Path (relativo al HTML)
            var headerImageNode = _htmlDoc.DocumentNode.SelectSingleNode("//div[contains(@class, 'js-open-screenshot-viewer')]/img[contains(@class, 'app-logo')]");
            if (headerImageNode != null)
            {
                string src = headerImageNode.GetAttributeValue("src", "");
                if (!string.IsNullOrEmpty(src))
                {
                    // Asumimos que la imagen está en una carpeta relativa al HTML
                    _parsedGameInfo.HeaderImagePath = Path.Combine(Path.GetDirectoryName(_selectedHtmlFilePath), src.TrimStart('.', '/').Replace('/', Path.DirectorySeparatorChar));
                }
            }
        }

        private void UpdateGameInfoUI()
        {
            lblGameNameValue.Text = _parsedGameInfo.Name ?? "(No encontrado)";
            lblAppIdValue.Text = _parsedGameInfo.AppId ?? "(No encontrado)";
            lblDeveloperValue.Text = _parsedGameInfo.Developer ?? "(No encontrado)";
            lblReleaseDateValue.Text = _parsedGameInfo.ReleaseDate ?? "(No encontrado)";

            if (!string.IsNullOrEmpty(_parsedGameInfo.HeaderImagePath) && File.Exists(_parsedGameInfo.HeaderImagePath))
            {
                try
                {
                    // Cargar la imagen. Usar un FileStream para evitar bloqueos del archivo.
                    using (FileStream stream = new FileStream(_parsedGameInfo.HeaderImagePath, FileMode.Open, FileAccess.Read))
                    {
                        picGameHeader.Image = Image.FromStream(stream);
                    }
                }
                catch (Exception ex)
                {
                    picGameHeader.Image = null;
                    // Podrías mostrar un mensaje si la imagen no carga, pero quizás es demasiado intrusivo.
                    // MessageBox.Show($"No se pudo cargar la imagen del header: {ex.Message}");
                    Console.WriteLine($"Error loading header image: {ex.Message}");
                }
            }
            else
            {
                picGameHeader.Image = null;
            }
        }

        private void ParseAchievementsList()
        {
            if (_htmlDoc == null) return;
            _parsedAchievements.Clear();
            lblStatus.Text = "Generating achievements..."; // Mensaje inicial
            Application.DoEvents();

            // Expresión XPath más general para encontrar los contenedores de logros.
            // Busca cualquier div que tenga una clase 'achievement' y un atributo 'id' que comience con 'achievement-'
            // Esto es menos dependiente de que esté dentro de un 'achievement_list' específico,
            // por si la estructura contenedora cambia.
            var achievementNodes = _htmlDoc.DocumentNode.SelectNodes("//div[contains(@class, 'achievement') and starts-with(@id, 'achievement-')]");

            if (achievementNodes == null || !achievementNodes.Any())
            {
                // Log para ver si el XPath principal falló
                Console.WriteLine("DEBUG: No se encontraron nodos de logros con XPath: //div[contains(@class, 'achievement') and starts-with(@id, 'achievement-')]");

                // Intento con la estructura original que me pasaste, por si acaso
                var achievementListContainer = _htmlDoc.DocumentNode.SelectSingleNode("//div[contains(@class, 'achievements_list')]");
                if (achievementListContainer != null)
                {
                    achievementNodes = achievementListContainer.SelectNodes(".//div[contains(@class, 'achievement') and @id]");
                    Console.WriteLine($"DEBUG: Intento 2 con //div[contains(@class, 'achievements_list')]... encontrado: {(achievementNodes?.Count ?? 0)} nodos.");
                }
                else
                {
                    Console.WriteLine("DEBUG: Contenedor 'achievements_list' tampoco encontrado.");
                }


                if (achievementNodes == null || !achievementNodes.Any())
                {
                    lblStatus.Text = "No achievement nodes were found in the HTML.";
                    MessageBox.Show("No achievement items could be found in the provided HTML file. Please verify that the HTML file is correct and contains the SteamDB achievement list.", "Parsing Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }

            Console.WriteLine($"DEBUG: Encontrados {achievementNodes.Count} nodos de logros potenciales.");

            int parsedCount = 0;
            foreach (var node in achievementNodes)
            {
                var achievement = new Form1.Achievement(); // Asegúrate de usar Form1.Achievement si está anidada

                // API Name (desde el ID del div)
                string idAttribute = node.Id;
                if (string.IsNullOrEmpty(idAttribute) || !idAttribute.StartsWith("achievement-"))
                {
                    Console.WriteLine($"DEBUG: Nodo de logro saltado, ID inválido: {idAttribute}");
                    continue; // Saltar este nodo si el ID no es el esperado
                }
                achievement.Name = idAttribute.Replace("achievement-", "");

                // Display Name
                var nameNode = node.SelectSingleNode(".//div[contains(@class, 'achievement_name')]");
                achievement.DisplayName = new Dictionary<string, string>
                {
                    { "english", nameNode != null ? HtmlEntity.DeEntitize(nameNode.InnerText.Trim()) : "N/A" }
                };

                // Description y Hidden
                var descNode = node.SelectSingleNode(".//div[contains(@class, 'achievement_desc')]");
                string descriptionText = "N/A";
                achievement.Hidden = "0";

                if (descNode != null)
                {
                    var spoilerNode = descNode.SelectSingleNode("./span[contains(@class, 'achievement_spoiler')]");
                    if (spoilerNode != null)
                    {
                        achievement.Hidden = "1";
                        descriptionText = HtmlEntity.DeEntitize(spoilerNode.InnerText.Trim());
                    }
                    else
                    {
                        var tempDescNode = descNode.Clone();
                        var iNode = tempDescNode.SelectSingleNode("./i[contains(text(),'Hidden achievement:')]"); // Ser más específico
                        if (iNode != null)
                        {
                            achievement.Hidden = "1";
                            // Remover el nodo 'i' y luego obtener el texto
                            iNode.Remove();
                            descriptionText = HtmlEntity.DeEntitize(tempDescNode.InnerText.Trim());
                        }
                        else
                        {
                            descriptionText = HtmlEntity.DeEntitize(tempDescNode.InnerText.Trim());
                        }
                    }
                }
                achievement.Description = new Dictionary<string, string>
                {
                    { "english", descriptionText }
                };

                // Icon
                var iconNode = node.SelectSingleNode(".//img[contains(@class, 'achievement_image') and not(contains(@class, 'achievement_image_small'))]");
                if (iconNode != null)
                {
                    string iconDataName = iconNode.GetAttributeValue("data-name", "");
                    string iconSrc = iconNode.GetAttributeValue("src", "");
                    if (!string.IsNullOrEmpty(iconDataName))
                    {
                        achievement.Icon = $"images/{iconDataName}";
                        achievement.SetIconUrl(iconSrc);
                    }
                    else if (!string.IsNullOrEmpty(iconSrc)) // Fallback si data-name no existe
                    {
                        string fileNameFromSrc = Path.GetFileName(new Uri(iconSrc, UriKind.RelativeOrAbsolute).LocalPath);
                        achievement.Icon = $"images/{fileNameFromSrc}";
                        achievement.SetIconUrl(iconSrc);
                    }
                }

                // Icon Gray
                var iconGrayNode = node.SelectSingleNode(".//img[contains(@class, 'achievement_image_small')]");
                if (iconGrayNode != null)
                {
                    string iconGrayDataName = iconGrayNode.GetAttributeValue("data-name", "");
                    string iconGraySrc = iconGrayNode.GetAttributeValue("src", "");
                    if (!string.IsNullOrEmpty(iconGrayDataName))
                    {
                        achievement.IconGray = $"images/{iconGrayDataName}";
                        achievement.SetIconGrayUrl(iconGraySrc);
                    }
                    else if (!string.IsNullOrEmpty(iconGraySrc)) // Fallback
                    {
                        string fileNameFromSrc = Path.GetFileName(new Uri(iconGraySrc, UriKind.RelativeOrAbsolute).LocalPath);
                        achievement.IconGray = $"images/{fileNameFromSrc}";
                        achievement.SetIconGrayUrl(iconGraySrc);
                    }
                }

                // Pequeño log por cada logro parseado
                Console.WriteLine($"DEBUG: Parseado Logro - API: {achievement.Name}, Nombre: {achievement.DisplayName["english"]}, Oculto: {achievement.Hidden}");
                _parsedAchievements.Add(achievement);
                parsedCount++;
            }

            lblAchievementsCountValue.Text = parsedCount.ToString(); // Actualizar con los realmente parseados
            if (parsedCount > 0)
            {
                lblStatus.Text = $"{parsedCount} generated achievements.";
            }
            else if (achievementNodes != null && achievementNodes.Any()) // Si encontró nodos pero no pudo parsear ninguno
            {
                lblStatus.Text = "Achievement data was found, but no information could be extracted.";
                MessageBox.Show("Elements that appear to be achievements were found, but their detailed information could not be extracted. The HTML structure may have changed.", "Generation advice", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            // El caso de 'No se encontraron nodos' ya se maneja arriba.
        }


        private async void btnGenerate_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedHtmlFilePath) || _parsedAchievements.Count == 0)
            {
                MessageBox.Show("Por favor, carga un archivo HTML con logros primero.", "Información", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string baseHtmlDirectory = Path.GetDirectoryName(_selectedHtmlFilePath);
            string outputDirectoryName = $"steam_settings";
            string outputDirectoryPath = Path.Combine(baseHtmlDirectory, outputDirectoryName);
            string imagesDirectoryPath = Path.Combine(outputDirectoryPath, "images");

            try
            {
                lblStatus.Text = "Generating...";
                progressBar.Visible = true;
                progressBar.Minimum = 0;
                progressBar.Maximum = _parsedAchievements.Count * 2 + 2; // *2 para icono y icono gris, +2 para JSON y appid
                progressBar.Value = 0;
                Application.DoEvents();


                Directory.CreateDirectory(outputDirectoryPath);
                Directory.CreateDirectory(imagesDirectoryPath);

                // Descargar imágenes
                using (HttpClient client = new HttpClient())
                {
                    // Añadir un User-Agent para evitar posibles bloqueos 403
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");

                    foreach (var ach in _parsedAchievements)
                    {
                        string currentIconUrl = ach.GetIconUrl();
                        if (!string.IsNullOrEmpty(currentIconUrl))
                        {
                            string iconFileName;
                            if (Uri.IsWellFormedUriString(currentIconUrl, UriKind.Absolute))
                            {
                                // Si es una URL absoluta, intentar obtener el nombre del archivo de ella
                                iconFileName = Path.GetFileName(new Uri(currentIconUrl).LocalPath);
                            }
                            else
                            {
                                // Si es una ruta relativa (o ya solo el nombre del archivo con "images/")
                                // simplemente tomar el nombre del archivo.
                                iconFileName = Path.GetFileName(currentIconUrl); // Esto manejará "images/file.jpg" -> "file.jpg"
                                                                                 // o "./folder/file.jpg" -> "file.jpg"
                            }

                            // Si después de todo, iconFileName sigue siendo problemático o vacío (ej. URL sin nombre de archivo claro)
                            // usar el fallback del nombre de archivo de ach.Icon (que viene de data-name)
                            if (string.IsNullOrEmpty(iconFileName) || iconFileName.Length > 100) // Longitud como heurística
                            {
                                iconFileName = Path.GetFileName(ach.Icon); // ach.Icon es "images/data-name.jpg"
                            }


                            string localIconPath = Path.Combine(imagesDirectoryPath, iconFileName);
                            await DownloadImageAsync(client, currentIconUrl, localIconPath, baseHtmlDirectory);
                            ach.Icon = $"images/{iconFileName}"; // Asegurar que la ruta en el JSON sea correcta
                        }
                        progressBar.Value++;
                        Application.DoEvents();

                        string currentIconGrayUrl = ach.GetIconGrayUrl();
                        if (!string.IsNullOrEmpty(currentIconGrayUrl))
                        {
                            string iconGrayFileName;
                            if (Uri.IsWellFormedUriString(currentIconGrayUrl, UriKind.Absolute))
                            {
                                iconGrayFileName = Path.GetFileName(new Uri(currentIconGrayUrl).LocalPath);
                            }
                            else
                            {
                                iconGrayFileName = Path.GetFileName(currentIconGrayUrl);
                            }

                            if (string.IsNullOrEmpty(iconGrayFileName) || iconGrayFileName.Length > 100)
                            {
                                iconGrayFileName = Path.GetFileName(ach.IconGray);
                            }

                            string localIconGrayPath = Path.Combine(imagesDirectoryPath, iconGrayFileName);
                            await DownloadImageAsync(client, currentIconGrayUrl, localIconGrayPath, baseHtmlDirectory);
                            ach.IconGray = $"images/{iconGrayFileName}";
                        }
                        progressBar.Value++;
                        Application.DoEvents();
                    }
                }

                // Crear achievements.json
                // Creamos una nueva lista para serializar solo las propiedades deseadas (sin IconUrl y IconGrayUrl)
                var achievementsToSerialize = _parsedAchievements.Select(ach => new Achievement
                {
                    Name = ach.Name,
                    DisplayName = ach.DisplayName,
                    Description = ach.Description,
                    Hidden = ach.Hidden,
                    Icon = ach.Icon,
                    IconGray = ach.IconGray
                }).ToList();

                string jsonContent = JsonConvert.SerializeObject(achievementsToSerialize, Formatting.Indented);
                File.WriteAllText(Path.Combine(outputDirectoryPath, "achievements.json"), jsonContent, Encoding.UTF8);
                progressBar.PerformStep();

                // Crear steam_appid.txt
                if (!string.IsNullOrEmpty(_parsedGameInfo.AppId) && _parsedGameInfo.AppId != "(Not found)")
                {
                    File.WriteAllText(Path.Combine(outputDirectoryPath, "steam_appid.txt"), _parsedGameInfo.AppId, Encoding.UTF8);
                }
                progressBar.PerformStep();

                MessageBox.Show($"Files successfully generated in:\n{outputDirectoryPath}", "Éxito", MessageBoxButtons.OK, MessageBoxIcon.Information);
                lblStatus.Text = "¡Process completed!";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during generation: {ex.Message}\n{ex.StackTrace}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                lblStatus.Text = "Error during generation.";
            }
            finally
            {
                progressBar.Visible = false;
            }
        }

        private async Task DownloadImageAsync(HttpClient client, string urlOrRelativePath, string outputPath, string baseHtmlDirectory)
        {
            try
            {
                string sourcePath;
                byte[] imageBytes;

                if (Uri.IsWellFormedUriString(urlOrRelativePath, UriKind.Absolute)) // Es una URL completa
                {
                    imageBytes = await client.GetByteArrayAsync(urlOrRelativePath);
                }
                else // Es una ruta relativa (desde el archivo HTML o su carpeta de assets)
                {
                    // Las rutas en el HTML descargado pueden ser como "./nombrecarpeta_files/imagen.jpg" o "nombrecarpeta_files/imagen.jpg"
                    // o directamente en la carpeta del HTML
                    string potentialPath1 = Path.Combine(baseHtmlDirectory, urlOrRelativePath.TrimStart('.', Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    string potentialPath2 = Path.Combine(Path.GetDirectoryName(_selectedHtmlFilePath), urlOrRelativePath.TrimStart('.', Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));


                    if (File.Exists(potentialPath1))
                    {
                        sourcePath = potentialPath1;
                    }
                    else if (File.Exists(potentialPath2))
                    {
                        sourcePath = potentialPath2;
                    }
                    else
                    {
                        // Si la ruta es solo el nombre del archivo (ej. lo extraído de data-name)
                        // y no es una URL, intentar encontrarlo en la carpeta de assets común.
                        // Esto es un fallback y puede necesitar ajuste.
                        string htmlFileNameWithoutExtension = Path.GetFileNameWithoutExtension(_selectedHtmlFilePath);
                        string assetsFolder = Path.Combine(baseHtmlDirectory, $"{htmlFileNameWithoutExtension}_files"); // Chrome suele crear esta carpeta
                        sourcePath = Path.Combine(assetsFolder, Path.GetFileName(urlOrRelativePath)); // Tomar solo el nombre de archivo

                        if (!File.Exists(sourcePath))
                        {
                            // Un intento más, si la URL era como "images/hash.jpg" del parseo y queremos copiar localmente
                            sourcePath = Path.Combine(baseHtmlDirectory, Path.GetDirectoryName(urlOrRelativePath), Path.GetFileName(urlOrRelativePath));
                            if (!File.Exists(sourcePath))
                            {
                                Console.WriteLine($"Imagen local no encontrada: {urlOrRelativePath} (intentado en {potentialPath1}, {potentialPath2} y {sourcePath})");
                                return; // No se pudo encontrar la imagen localmente
                            }
                        }
                    }
                    imageBytes = File.ReadAllBytes(sourcePath);
                }

                File.WriteAllBytes(outputPath, imageBytes);
            }
            catch (HttpRequestException httpEx)
            {
                Console.WriteLine($"Error HTTP descargando {urlOrRelativePath}: {httpEx.Message}");
                if (!Uri.IsWellFormedUriString(urlOrRelativePath, UriKind.Absolute))
                {
                    TryCopyLocalImage(urlOrRelativePath, outputPath, baseHtmlDirectory); // Quitar await
                }
            }
            catch (IOException ioEx)
            {
                Console.WriteLine($"Error IO con {urlOrRelativePath}: {ioEx.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error general descargando/copiando {urlOrRelativePath}: {ex.Message}");
            }
        }

        private async Task TryCopyLocalImage(string originalSrc, string outputPath, string baseHtmlDirectory)
        {
            // Esta función se llama si la descarga de URL falla, para intentar copiar
            // la imagen desde la estructura de carpetas local que Chrome crea.
            try
            {
                // originalSrc puede ser algo como "https://steamdb.info/...", "images/...", o "./game_files/..."
                string localFileName = Path.GetFileName(new Uri(originalSrc, UriKind.RelativeOrAbsolute).LocalPath);
                if (string.IsNullOrEmpty(localFileName) && originalSrc.StartsWith("images/")) // Caso como el del JSON de ejemplo
                {
                    localFileName = originalSrc.Substring("images/".Length);
                }
                else if (string.IsNullOrEmpty(localFileName))
                {
                    Console.WriteLine($"No se pudo determinar el nombre de archivo local para {originalSrc}");
                    return;
                }


                // Intentar buscar en la carpeta de assets generada por Chrome
                string htmlFileDirectory = Path.GetDirectoryName(_selectedHtmlFilePath);
                string assetsFolderName = Path.GetFileNameWithoutExtension(_selectedHtmlFilePath) + "_files";
                string localAssetPath = Path.Combine(htmlFileDirectory, assetsFolderName, localFileName);

                if (File.Exists(localAssetPath))
                {
                    File.Copy(localAssetPath, outputPath, true);
                    Console.WriteLine($"Imagen copiada localmente: {localAssetPath} a {outputPath}");
                }
                else
                {
                    // Fallback si está directamente en la carpeta del html
                    string directPath = Path.Combine(htmlFileDirectory, localFileName);
                    if (File.Exists(directPath))
                    {
                        File.Copy(directPath, outputPath, true);
                        Console.WriteLine($"Imagen copiada localmente: {directPath} a {outputPath}");
                    }
                    else
                    {
                        Console.WriteLine($"Fallback: Imagen local no encontrada para copia: {originalSrc} (buscado como {localAssetPath} y {directPath})");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error copiando imagen local {originalSrc}: {ex.Message}");
            }
        }

        private void lblStep1_Click(object sender, EventArgs e)
        {

        }
    }

    // Mueve estas clases fuera de Form1 si prefieres, o déjalas como clases anidadas.
    // Para este ejemplo, las he dejado anidadas para simplicidad, pero
    // es buena práctica tenerlas en archivos separados para proyectos más grandes.
    public static class AchievementExtensions // Clase helper para las URLs de iconos
    {
        // Usaremos campos en lugar de propiedades para que no se serialicen a JSON
        // si accidentalmente serializamos la clase Achievement directamente con estos campos.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Form1.Achievement, string> iconUrls =
                    new System.Runtime.CompilerServices.ConditionalWeakTable<Form1.Achievement, string>();

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Form1.Achievement, string> iconGrayUrls =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Form1.Achievement, string>();

        public static string GetIconUrl(this Form1.Achievement achievement)
        {
            return iconUrls.TryGetValue(achievement, out var url) ? url : null;
        }
        public static void SetIconUrl(this Form1.Achievement achievement, string url)
        {
            // Para .NET Framework, ConditionalWeakTable no tiene AddOrUpdate.
            // Si la clave ya existe, Add lanzará una excepción.
            // La opción más segura es remover si existe y luego añadir.
            // O, si sabes que solo la establecerás una vez, solo usa Add.
            // Para este caso, como podríamos "actualizarla" si encontramos una mejor URL,
            // es mejor un enfoque de remover y añadir.
            if (iconUrls.TryGetValue(achievement, out _))
            {
                iconUrls.Remove(achievement);
            }
            iconUrls.Add(achievement, url);
        }

        public static string GetIconGrayUrl(this Form1.Achievement achievement)
        {
            return iconGrayUrls.TryGetValue(achievement, out var url) ? url : null;
        }
        public static void SetIconGrayUrl(this Form1.Achievement achievement, string url)
        {
            if (iconGrayUrls.TryGetValue(achievement, out _))
            {
                iconGrayUrls.Remove(achievement);
            }
            iconGrayUrls.Add(achievement, url);
        }
    }
}