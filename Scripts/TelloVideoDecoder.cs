using System;
using System.Collections.Generic;
using UnityEngine;

namespace TelloQuest
{
    /// <summary>
    /// Wraps PopH264's decode API to hardware-decode the H.264 access units from
    /// TelloVideoReceiver into live textures.
    ///
    /// ------------------------------------------------------------------
    /// CE QUI A CHANGE :
    ///
    /// 1. DRAIN COMPLET. GetNextFrame() n'etait appele qu'UNE fois par Update().
    ///    Avec le decodage threade, MediaCodec sort ses frames par rafales : des
    ///    que la file de sortie prenait de l'avance on ne la rattrapait jamais,
    ///    et la latence montait de facon monotone pendant tout le vol. On draine
    ///    maintenant jusqu'a epuisement et on ne garde que la plus recente.
    ///
    /// 2. GATE IDR. On attendait un SPS puis on poussait tout, y compris des
    ///    P-slices referencant des frames jamais decodees -> premiere seconde en
    ///    bouillie de macroblocs. On attend maintenant SPS *puis* IDR.
    ///
    /// 3. RESET D'ETAT. OnDisable() disposait le decodeur mais ne remettait a
    ///    zero ni sawFirstSps ni nextFrameNumber : au re-enable (ou apres une
    ///    pause casque) on alimentait un decodeur neuf avec des P-frames et
    ///    l'image ne revenait jamais.
    ///
    /// 4. WATCHDOG. Si des frames entrent mais que plus rien ne sort pendant
    ///    DecoderStallTimeoutSeconds, on recree le decodeur. C'est ce qui evitait
    ///    de devoir relancer l'app apres une coupure Wi-Fi.
    ///
    /// 5. PARSING DU SPS. La resolution reelle, la plage (full/limited) et la
    ///    matrice (BT.601/709) sont maintenant lues dans le SPS au lieu d'etre
    ///    supposees. Ca alimente le crop du shader (padding MediaCodec) et
    ///    remplace les 960x720 codes en dur du recorder.
    ///
    /// 6. LOGS. Le bloc [DIAG] permanent est passe derriere verboseDiagnostics
    ///    (decoche par defaut) : c'etait 1 Debug.Log/seconde vers logcat, avec
    ///    interpolation de chaine, en permanence.
    /// ------------------------------------------------------------------
    /// </summary>
    public class TelloVideoDecoder : MonoBehaviour
    {
        [SerializeField] private TelloVideoReceiver videoReceiver;
        [Tooltip("Let PopH264 push frames on its own background thread, decoupled from Unity's Update rate.")]
        [SerializeField] private bool threadedDecoding = true;

        [Header("=== RECOVERY ===")]
        [Tooltip("Si des access units sont poussees mais qu'aucune frame ne sort pendant ce delai, le decodeur est recree. 0 = desactive.")]
        [SerializeField] private float decoderStallTimeoutSeconds = 3f;

        [Header("=== DIAGNOSTICS ===")]
        [Tooltip("Log une ligne de compteurs par seconde. A laisser decoche en vol : Debug.Log vers logcat coute cher sur Quest.")]
        [SerializeField] private bool verboseDiagnostics = false;

        private PopH264.Decoder decoder;
        private int nextFrameNumber;

        private List<Texture2D> planes;
        private List<PopH264.PixelFormat> pixelFormats;

        public Texture2D VideoTexture { get; private set; }
        public bool IsYuvNv12 { get; private set; }
        public Texture2D YPlane { get; private set; }
        public Texture2D UVPlane { get; private set; }
        public bool UvChannelsSwapped { get; private set; }
        public long FramesDecodedTotal { get; private set; }
        public long FramesPushFailedTotal { get; private set; }
        /// <summary>Frames decodees mais jamais affichees parce qu'une plus recente
        /// etait deja disponible dans le meme Update. Un chiffre eleve signifie que le
        /// decodeur livre par rafales - normal - et non qu'on perd de l'information.</summary>
        public long FramesDroppedToCatchUpTotal { get; private set; }
        public float LastFrameDecodedTime { get; private set; }

        /// <summary>Resolution reelle de l'image, lue dans le SPS (et non la taille
        /// de la texture, qui peut etre plus grande a cause de l'alignement
        /// MediaCodec). 0 tant qu'aucun SPS n'a ete decode.</summary>
        public int VideoWidth { get; private set; }
        public int VideoHeight { get; private set; }

        /// <summary>Lus dans la VUI du SPS quand elle est presente. Valeurs par
        /// defaut conservatrices sinon : limited range, BT.601 (convention pour
        /// une source de cette resolution sans VUI).</summary>
        public bool IsFullRange { get; private set; }
        public bool IsBt709 { get; private set; }
        public bool ColorInfoFromVui { get; private set; }

        /// <summary>Fraction de la texture reellement occupee par l'image utile.
        /// (1,1) quand il n'y a pas de padding. Consomme par TelloVideoDisplay
        /// pour piloter _CropScale dans le shader.</summary>
        public Vector2 CropScale { get; private set; } = Vector2.one;

        /// <summary>Runtime-adjustable depuis l'ecran de parametres video.</summary>
        public float DecoderStallTimeoutSeconds { get => decoderStallTimeoutSeconds; set => decoderStallTimeoutSeconds = Mathf.Max(0f, value); }

        public byte[] CapturedSps { get; private set; }
        public byte[] CapturedPps { get; private set; }

        /// <summary>Raised on the main thread whenever the video textures have fresh pixel data.</summary>
        public event Action OnTextureUpdated;

        /// <summary>Raised once the real video dimensions / colour info are known,
        /// or whenever they change (resolution switch mid-stream).</summary>
        public event Action OnVideoFormatChanged;

        private bool loggedFormatWarning;
        private bool loggedUnhandledFormatWarning;
        private bool loggedPlaneSizes;

        private bool sawFirstSps;
        private bool sawFirstIdr;
        private long discardedBeforeSync;
        private bool loggedStallWarning;

        private float lastPushTime;
        private int recreateCount;

        private long diagnosticFrameNumberHits;
        private float diagnosticLogTimer;

        private void Awake()
        {
            if (videoReceiver == null) videoReceiver = GetComponent<TelloVideoReceiver>();
        }

        private void OnEnable()
        {
            if (videoReceiver != null) videoReceiver.OnFrameReady += HandleFrameReady;
            CreateDecoder();
        }

        private void OnDisable()
        {
            if (videoReceiver != null) videoReceiver.OnFrameReady -= HandleFrameReady;
            DestroyDecoder();
        }

        // =================================================================
        // CYCLE DE VIE DU DECODEUR
        // =================================================================
        private void CreateDecoder()
        {
            try
            {
                decoder = new PopH264.Decoder(null, threadedDecoding); // null = DecoderParams par defaut
            }
            catch (Exception e)
            {
                // Cause la plus probable sur Android : libPopH264.so n'a pas pu
                // etre chargee. Sans ce catch, ce serait un ecran noir silencieux.
                Debug.LogError($"[TelloVideoDecoder] Failed to create PopH264 decoder - the native plugin likely failed to load: {e.Message}");
                decoder = null;
                return;
            }

            // Tout l'etat de synchro doit repartir de zero avec le decodeur :
            // un decodeur neuf n'a jamais vu de SPS, donc reouvrir la porte est
            // obligatoire (c'etait le bug du re-enable).
            nextFrameNumber = 0;
            sawFirstSps = false;
            sawFirstIdr = false;
            discardedBeforeSync = 0;
            loggedStallWarning = false;
            lastPushTime = Time.time;
            LastFrameDecodedTime = Time.time;
            planes = null;
            pixelFormats = null;
        }

        private void DestroyDecoder()
        {
            try { decoder?.Dispose(); }
            catch (Exception e) { Debug.LogWarning($"[TelloVideoDecoder] Error disposing decoder: {e.Message}"); }
            decoder = null;
        }

        private void RecreateDecoder(string reason)
        {
            recreateCount++;
            Debug.LogWarning($"[TelloVideoDecoder] Recreating decoder (#{recreateCount}): {reason}");
            DestroyDecoder();
            CreateDecoder();
            // CapturedSps/CapturedPps sont volontairement conserves : le recorder
            // en a besoin et ils ne changent pas d'une instance a l'autre.
        }

        // =================================================================
        // ENTREE : access units
        // =================================================================
        private void HandleFrameReady(byte[] annexBFrame)
        {
            if (decoder == null) return;

            // Un decodeur H.264 ne peut rien produire a partir de P/B-slices
            // seules. Si notre socket a commence a ecouter apres la rafale
            // SPS/PPS/IDR initiale du Tello, PushFrameData accepte tout mais
            // GetNextFrame ne rendra jamais rien. On jette donc tout jusqu'a
            // avoir vu un SPS, PUIS un IDR.
            //
            // (Une variante exigeant SPS ET PPS dans la MEME access unit avait
            // ete essayee et annulee : sur ce flux ils ne sont pas fiablement
            // apparies dans une meme AU reassemblee, donc ce test pouvait caler
            // indefiniment. Le SPS seul reste le declencheur ; l'IDR est verifie
            // separement juste apres, ce qui est plus sur sans risquer ce blocage.)
            if (!sawFirstSps)
            {
                if (!ContainsNalType(annexBFrame, 7))
                {
                    NoteDiscardedBeforeSync();
                    return;
                }
                sawFirstSps = true;
                CapturedSps = ExtractNal(annexBFrame, 7);
                ParseSpsMetadata(CapturedSps);
            }

            // PPS pas garanti dans la meme AU que le SPS - cherche independamment.
            if (CapturedPps == null)
            {
                byte[] pps = ExtractNal(annexBFrame, 8);
                if (pps != null) CapturedPps = pps;
            }

            // On attend une image cle avant de commencer a alimenter : sans ca,
            // les premieres AU sont des P-slices qui referencent des frames
            // jamais decodees -> macroblocs baveux pendant ~1s.
            if (!sawFirstIdr)
            {
                if (!ContainsNalType(annexBFrame, 5))
                {
                    NoteDiscardedBeforeSync();
                    return;
                }
                sawFirstIdr = true;
            }

            bool ok = decoder.PushFrameData(annexBFrame, nextFrameNumber);
            if (ok) nextFrameNumber++;
            else FramesPushFailedTotal++;

            lastPushTime = Time.time;
        }

        private void NoteDiscardedBeforeSync()
        {
            discardedBeforeSync++;
            if (!loggedStallWarning && discardedBeforeSync > 500)
            {
                loggedStallWarning = true;
                Debug.LogWarning($"[TelloVideoDecoder] Still waiting for SPS+IDR after {discardedBeforeSync} discarded access units - video will not decode until both are found. This is not normal.");
            }
        }

        // =================================================================
        // SORTIE : textures decodees
        // =================================================================
        private void Update()
        {
            if (decoder == null) return;

            // Drain complet : on consomme TOUT ce que le decodeur a en attente et
            // on ne garde que la derniere frame. Ne prendre qu'une frame par
            // Update() laissait la file de sortie grossir indefiniment.
            // On compte CHAQUE frame reellement produite par le decodeur, pas le
            // nombre d'Update() ayant abouti a un affichage. Avec le drain, plusieurs
            // frames peuvent sortir dans le meme Update (MediaCodec livre par
            // rafales) : ne compter qu'une fois par Update sous-estimait fortement la
            // cadence reelle - c'est ce qui faisait afficher ~12 fps a
            // TelloStatusPanel alors que le flux tournait bien a ~30.
            int framesThisUpdate = 0;
            int guard = 0;
            while (guard++ < 16)
            {
                int? frameNumber = decoder.GetNextFrame(ref planes, ref pixelFormats);
                if (!frameNumber.HasValue) break;
                diagnosticFrameNumberHits++;
                if (planes == null || planes.Count == 0) continue;
                framesThisUpdate++;
            }

            if (framesThisUpdate > 0 && planes != null && planes.Count > 0)
            {
                // Les frames intermediaires sont decodees mais non affichees : seule
                // la plus recente l'est. C'est voulu (fraicheur > completude), et
                // FramesDroppedToCatchUpTotal permet de savoir si ca arrive souvent.
                if (framesThisUpdate > 1) FramesDroppedToCatchUpTotal += framesThisUpdate - 1;
                FramesDecodedTotal += framesThisUpdate;
                PublishFrame();
            }

            RunWatchdog();
            RunDiagnosticLog();
        }

        private void PublishFrame()
        {
            bool isDirectRgba = planes.Count == 1 &&
                (pixelFormats[0] == PopH264.PixelFormat.RGBA || pixelFormats[0] == PopH264.PixelFormat.BGRA);

            bool isNv12Yuv = planes.Count == 2 &&
                pixelFormats[0] == PopH264.PixelFormat.Greyscale &&
                (pixelFormats[1] == PopH264.PixelFormat.ChromaUV_88 || pixelFormats[1] == PopH264.PixelFormat.ChromaVU_88);

            if (isDirectRgba)
            {
                IsYuvNv12 = false;
                VideoTexture = planes[0];
                ApplyPlaneSampling(planes[0]);
                UpdateCropScale(planes[0]);
                LastFrameDecodedTime = Time.time;
                OnTextureUpdated?.Invoke();

                if (pixelFormats[0] == PopH264.PixelFormat.BGRA && !loggedFormatWarning)
                {
                    loggedFormatWarning = true;
                    Debug.LogWarning("[TelloVideoDecoder] Decoder outputs BGRA - a plain Unlit material will show red/blue swapped.");
                }
                return;
            }

            if (isNv12Yuv)
            {
                IsYuvNv12 = true;
                UvChannelsSwapped = pixelFormats[1] == PopH264.PixelFormat.ChromaVU_88;
                YPlane = planes[0];
                UVPlane = planes[1];
                ApplyPlaneSampling(planes[0]);
                ApplyPlaneSampling(planes[1]);
                UpdateCropScale(planes[0]);
                LastFrameDecodedTime = Time.time;

                if (!loggedPlaneSizes)
                {
                    loggedPlaneSizes = true;
                    // Log unique et volontairement conserve meme hors mode verbeux :
                    // c'est l'information qui permet de confirmer (ou d'infirmer) la
                    // presence de padding, et elle ne coute qu'une ligne, une fois.
                    Debug.Log($"[TelloVideoDecoder] Plane sizes: Y={planes[0].width}x{planes[0].height} ({planes[0].format}), " +
                              $"UV={planes[1].width}x{planes[1].height} ({planes[1].format}); " +
                              $"SPS says {VideoWidth}x{VideoHeight} -> cropScale={CropScale.x:F4},{CropScale.y:F4}");
                }

                OnTextureUpdated?.Invoke();
                return;
            }

            if (!loggedUnhandledFormatWarning)
            {
                loggedUnhandledFormatWarning = true;
                Debug.LogWarning($"[TelloVideoDecoder] Decoder returned {planes.Count} plane(s), first in {pixelFormats[0]} format - unhandled combination.");
            }
        }

        /// <summary>Bilineaire + clamp sur les plans : le Point filtering donne un
        /// chroma en escalier une fois agrandi, et le Repeat fait baver le bord
        /// droit sur le bord gauche.</summary>
        private static void ApplyPlaneSampling(Texture2D tex)
        {
            if (tex == null) return;
            if (tex.filterMode != FilterMode.Bilinear) tex.filterMode = FilterMode.Bilinear;
            if (tex.wrapMode != TextureWrapMode.Clamp) tex.wrapMode = TextureWrapMode.Clamp;
        }

        private void UpdateCropScale(Texture2D reference)
        {
            if (reference == null || VideoWidth <= 0 || VideoHeight <= 0) return;
            Vector2 next = new Vector2(
                Mathf.Clamp01(VideoWidth / (float)reference.width),
                Mathf.Clamp01(VideoHeight / (float)reference.height));
            if ((next - CropScale).sqrMagnitude > 1e-8f)
            {
                CropScale = next;
                OnVideoFormatChanged?.Invoke();
            }
        }

        // =================================================================
        // WATCHDOG
        // =================================================================
        private void RunWatchdog()
        {
            if (decoderStallTimeoutSeconds <= 0f) return;
            if (!sawFirstIdr) return; // pas encore alimente, rien a surveiller

            bool feeding = Time.time - lastPushTime < 1f;
            bool producing = Time.time - LastFrameDecodedTime < decoderStallTimeoutSeconds;

            if (feeding && !producing)
                RecreateDecoder($"no decoded frame for {decoderStallTimeoutSeconds:F1}s while access units were still arriving");
        }

        // =================================================================
        // PARSING SPS (resolution reelle + VUI couleur)
        // =================================================================
        private void ParseSpsMetadata(byte[] spsWithStartCode)
        {
            if (spsWithStartCode == null) return;
            try
            {
                byte[] rbsp = StripStartCodeAndEmulationBytes(spsWithStartCode);
                if (rbsp == null || rbsp.Length < 4) return;

                var br = new BitReader(rbsp);
                br.SkipBits(8); // en-tete NAL (StripStartCode... le conserve volontairement)
                int profileIdc = br.ReadBits(8);
                br.SkipBits(8);  // constraint flags + reserved
                br.SkipBits(8);  // level_idc
                br.ReadUE();     // seq_parameter_set_id

                int chromaFormatIdc = 1; // 4:2:0 par defaut (baseline/main)
                bool separateColourPlane = false;

                if (profileIdc == 100 || profileIdc == 110 || profileIdc == 122 || profileIdc == 244 ||
                    profileIdc == 44 || profileIdc == 83 || profileIdc == 86 || profileIdc == 118 ||
                    profileIdc == 128 || profileIdc == 138 || profileIdc == 139 || profileIdc == 134 || profileIdc == 135)
                {
                    chromaFormatIdc = br.ReadUE();
                    if (chromaFormatIdc == 3) separateColourPlane = br.ReadBit() == 1;
                    br.ReadUE(); // bit_depth_luma_minus8
                    br.ReadUE(); // bit_depth_chroma_minus8
                    br.SkipBits(1); // qpprime_y_zero_transform_bypass_flag
                    if (br.ReadBit() == 1) // seq_scaling_matrix_present_flag
                    {
                        int listCount = (chromaFormatIdc != 3) ? 8 : 12;
                        for (int i = 0; i < listCount; i++)
                            if (br.ReadBit() == 1) SkipScalingList(br, i < 6 ? 16 : 64);
                    }
                }

                br.ReadUE(); // log2_max_frame_num_minus4
                int picOrderCntType = br.ReadUE();
                if (picOrderCntType == 0)
                {
                    br.ReadUE(); // log2_max_pic_order_cnt_lsb_minus4
                }
                else if (picOrderCntType == 1)
                {
                    br.SkipBits(1); // delta_pic_order_always_zero_flag
                    br.ReadSE();    // offset_for_non_ref_pic
                    br.ReadSE();    // offset_for_top_to_bottom_field
                    int n = br.ReadUE();
                    for (int i = 0; i < n; i++) br.ReadSE();
                }

                br.ReadUE();    // max_num_ref_frames
                br.SkipBits(1); // gaps_in_frame_num_value_allowed_flag

                int picWidthInMbsMinus1 = br.ReadUE();
                int picHeightInMapUnitsMinus1 = br.ReadUE();
                int frameMbsOnlyFlag = br.ReadBit();
                if (frameMbsOnlyFlag == 0) br.SkipBits(1); // mb_adaptive_frame_field_flag
                br.SkipBits(1); // direct_8x8_inference_flag

                int cropLeft = 0, cropRight = 0, cropTop = 0, cropBottom = 0;
                if (br.ReadBit() == 1) // frame_cropping_flag
                {
                    cropLeft = br.ReadUE();
                    cropRight = br.ReadUE();
                    cropTop = br.ReadUE();
                    cropBottom = br.ReadUE();
                }

                int subWidthC = 1, subHeightC = 1;
                if (!separateColourPlane)
                {
                    if (chromaFormatIdc == 1) { subWidthC = 2; subHeightC = 2; }
                    else if (chromaFormatIdc == 2) { subWidthC = 2; subHeightC = 1; }
                }
                int cropUnitX = (chromaFormatIdc == 0 || separateColourPlane) ? 1 : subWidthC;
                int cropUnitY = ((chromaFormatIdc == 0 || separateColourPlane) ? 1 : subHeightC) * (2 - frameMbsOnlyFlag);

                int width = (picWidthInMbsMinus1 + 1) * 16 - cropUnitX * (cropLeft + cropRight);
                int height = (2 - frameMbsOnlyFlag) * (picHeightInMapUnitsMinus1 + 1) * 16 - cropUnitY * (cropTop + cropBottom);

                bool fullRange = false, bt709 = false, fromVui = false;
                if (br.ReadBit() == 1) // vui_parameters_present_flag
                {
                    if (br.ReadBit() == 1) // aspect_ratio_info_present_flag
                    {
                        int aspectRatioIdc = br.ReadBits(8);
                        if (aspectRatioIdc == 255) br.SkipBits(32); // sar_width + sar_height
                    }
                    if (br.ReadBit() == 1) br.SkipBits(1); // overscan_info -> overscan_appropriate_flag
                    if (br.ReadBit() == 1) // video_signal_type_present_flag
                    {
                        br.SkipBits(3); // video_format
                        fullRange = br.ReadBit() == 1;
                        fromVui = true;
                        if (br.ReadBit() == 1) // colour_description_present_flag
                        {
                            br.SkipBits(8); // colour_primaries
                            br.SkipBits(8); // transfer_characteristics
                            int matrixCoefficients = br.ReadBits(8);
                            bt709 = matrixCoefficients == 1;
                        }
                    }
                }

                if (width > 0 && height > 0)
                {
                    bool changed = width != VideoWidth || height != VideoHeight ||
                                   fullRange != IsFullRange || bt709 != IsBt709;
                    VideoWidth = width;
                    VideoHeight = height;
                    IsFullRange = fullRange;
                    IsBt709 = bt709;
                    ColorInfoFromVui = fromVui;
                    Debug.Log($"[TelloVideoDecoder] SPS: {width}x{height}, profile={profileIdc}, " +
                              $"range={(fullRange ? "full" : "limited")}, matrix={(bt709 ? "BT.709" : "BT.601")}" +
                              $"{(fromVui ? "" : " (pas de VUI - valeurs par defaut)")}");
                    if (changed) OnVideoFormatChanged?.Invoke();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TelloVideoDecoder] Could not parse SPS ({e.Message}) - falling back to texture dimensions and BT.601 limited range.");
            }
        }

        private static void SkipScalingList(BitReader br, int size)
        {
            int lastScale = 8, nextScale = 8;
            for (int j = 0; j < size; j++)
            {
                if (nextScale != 0)
                {
                    int delta = br.ReadSE();
                    nextScale = (lastScale + delta + 256) % 256;
                }
                lastScale = (nextScale == 0) ? lastScale : nextScale;
            }
        }

        /// <summary>Retire le start code Annex-B puis les octets d'emulation
        /// prevention (0x000003 -> 0x0000). Le premier octet du resultat est
        /// l'en-tete NAL, que le parser saute lui-meme.</summary>
        private static byte[] StripStartCodeAndEmulationBytes(byte[] nal)
        {
            int start = 0;
            while (start + 2 < nal.Length)
            {
                if (nal[start] == 0 && nal[start + 1] == 0 && nal[start + 2] == 1) { start += 3; break; }
                start++;
            }
            if (start >= nal.Length) return null;

            var output = new List<byte>(nal.Length - start);
            int zeros = 0;
            for (int i = start; i < nal.Length; i++)
            {
                byte b = nal[i];
                if (zeros >= 2 && b == 0x03) { zeros = 0; continue; } // octet d'emulation
                output.Add(b);
                zeros = (b == 0) ? zeros + 1 : 0;
            }
            return output.ToArray();
        }

        private class BitReader
        {
            private readonly byte[] data;
            private int bitPosition;

            public BitReader(byte[] data) { this.data = data; }

            public int ReadBit()
            {
                int byteIndex = bitPosition >> 3;
                if (byteIndex >= data.Length) throw new IndexOutOfRangeException("SPS ended early");
                int bit = (data[byteIndex] >> (7 - (bitPosition & 7))) & 1;
                bitPosition++;
                return bit;
            }

            public int ReadBits(int count)
            {
                int value = 0;
                for (int i = 0; i < count; i++) value = (value << 1) | ReadBit();
                return value;
            }

            public void SkipBits(int count) { for (int i = 0; i < count; i++) ReadBit(); }

            /// <summary>Exp-Golomb non signe.</summary>
            public int ReadUE()
            {
                int leadingZeros = 0;
                while (ReadBit() == 0)
                {
                    leadingZeros++;
                    if (leadingZeros > 32) throw new InvalidOperationException("Malformed exp-Golomb code");
                }
                if (leadingZeros == 0) return 0;
                return (1 << leadingZeros) - 1 + ReadBits(leadingZeros);
            }

            /// <summary>Exp-Golomb signe.</summary>
            public int ReadSE()
            {
                int k = ReadUE();
                return (k % 2 == 0) ? -(k / 2) : (k + 1) / 2;
            }
        }

        // =================================================================
        // SCAN NAL
        // =================================================================
        private static bool ContainsNalType(byte[] data, int nalType)
        {
            for (int i = 0; i < data.Length - 3; i++)
            {
                if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
                {
                    int nalStart = i + 3;
                    if (nalStart < data.Length && (data[nalStart] & 0x1F) == nalType) return true;
                }
            }
            return false;
        }

        private static byte[] ExtractNal(byte[] data, int nalType)
        {
            for (int i = 0; i < data.Length - 3; i++)
            {
                if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
                {
                    int nalStart = i + 3;
                    if (nalStart >= data.Length || (data[nalStart] & 0x1F) != nalType) continue;

                    int codeStart = (i > 0 && data[i - 1] == 0) ? i - 1 : i;

                    int nalEnd = data.Length;
                    for (int j = nalStart; j < data.Length - 2; j++)
                    {
                        if (data[j] == 0 && data[j + 1] == 0 && data[j + 2] == 1) { nalEnd = j; break; }
                    }
                    while (nalEnd > codeStart && data[nalEnd - 1] == 0) nalEnd--;

                    byte[] result = new byte[nalEnd - codeStart];
                    Array.Copy(data, codeStart, result, 0, result.Length);
                    return result;
                }
            }
            return null;
        }

        // =================================================================
        // DIAGNOSTICS (optionnels)
        // =================================================================
        private void RunDiagnosticLog()
        {
            if (!verboseDiagnostics) return;
            diagnosticLogTimer += Time.deltaTime;
            if (diagnosticLogTimer < 1f) return;
            diagnosticLogTimer = 0f;

            Debug.Log($"[TelloVideoDecoder][DIAG] pushed={nextFrameNumber} pushFailed={FramesPushFailedTotal} " +
                      $"discardedBeforeSync={discardedBeforeSync} gnfHits={diagnosticFrameNumberHits} " +
                      $"decoded={FramesDecodedTotal} recreates={recreateCount}");
        }
    }
}
