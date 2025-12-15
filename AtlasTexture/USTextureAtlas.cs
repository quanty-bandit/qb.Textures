using System;
using UnityEngine;

namespace qb.Atlas
{
    /// <summary>
    /// Unique size texture atlas
    /// </summary>
    public class USTextureAtlas
    {
        int maxWidth = 1024;
        public enum PowerOfTwoWidth
        {
            _128 = 128,
            _256 = 256,
            _512 = 512,
            _1024 = 1024,
            _2048 = 2048
        }

        Texture2D atlasTexture;
        public Texture2D AtlasTexture => atlasTexture;

        Vector2[][] framesUV;
        public Vector2[][] FramesUV => framesUV;

        int framesCount;
        int frameWidth, frameHeight, padding;
        public int FramesCount => framesCount;
        public int FrameWidth => frameWidth;
        public int FrameHeight => frameHeight;

        public float FrameHorizontalRatio => frameWidth > 0 ? frameWidth / frameHeight : 1;
        public float FrameVerticalRatio => frameWidth > 0 ? (float)frameHeight / frameWidth : 1;

        int completeFrameCount;
        int width, height;

        int[] frames;
        public int[] Frames=> frames;
        int bx, by;

        public bool IsCompleted => completeFrameCount == framesCount;

        public USTextureAtlas(Texture2D atlasTexture, int[] bottomTopVertices)
        {
            int verticesEntriesCount = bottomTopVertices.Length;
            if (verticesEntriesCount < 4)
                throw new Exception("The number of entries from bottomTopVectices must be at least 4");
            if (verticesEntriesCount % 2 != 0)
                throw new Exception("The number of entries from bottomTopVectices must be even");

            this.atlasTexture = atlasTexture;

            width = atlasTexture.width;
            height = atlasTexture.height;

            framesCount = verticesEntriesCount / 4;
            frameWidth = bottomTopVertices[2] - bottomTopVertices[0];
            frameHeight = bottomTopVertices[3] - bottomTopVertices[1];

            this.frames = bottomTopVertices;
            GenerateFrameUVFromTBVertices();
        }

        void GenerateFrameUVFromTBVertices()
        {

            framesUV = new Vector2[framesCount][];
            int k = 0;
            for (int i = 0; i < framesCount; i++)
            {
                int x0 = frames[k++];
                int y0 = frames[k++];
                int x1 = frames[k++];
                int y1 = frames[k++];

                float u0 = ((float)x0 / width);
                float v0 = ((float)y0 / height);

                float u1 = ((float)x1 / width);
                float v1 = ((float)y1 / height);

                float cx = u0 + (u1 - u0) / 2f;
                float cy = v0 + (v1 - v0) / 2f;

                u0 += (cx - u0) * 0.017f;
                v0 += (cy - v0) * 0.017f;
                u1 += (cx - u1) * 0.017f;
                v1 += (cy - v1) * 0.017f;

                framesUV[i] = new Vector2[] { new Vector2(u0, v0), new Vector2(u1, v1) };
            }
        }
        public USTextureAtlas(int textureCount, int textureWidth, int textureHeight, PowerOfTwoWidth maxTextureAtlasWidth = PowerOfTwoWidth._1024, int padding = 0)
        {
            this.framesCount = textureCount;
            this.frameWidth = textureWidth;
            this.frameHeight = textureHeight;
            maxWidth = (int)maxTextureAtlasWidth;

            width = textureWidth * textureCount + padding * (textureCount - 1);
            if (width > maxWidth)
            {
                int lineCount = Mathf.RoundToInt((float)width / maxWidth);
                int textureByLine = Mathf.FloorToInt((float)maxWidth / (textureWidth + padding));
                int tc = textureByLine * lineCount;
                if (tc < framesCount)
                {
                    int mt = textureCount - tc;
                    lineCount += Mathf.RoundToInt((float)mt / textureByLine) + 1;
                }
                height = textureHeight * lineCount + padding * (lineCount - 1);
                width = maxWidth;
            }
            else
            {
                int w = width;
                for (int i = 4; i < 11; i++)
                {
                    float p2 = Mathf.Pow(2, i);
                    if (p2 > width)
                    {
                        w = (int)p2;
                        break;
                    }
                }
                float p = w - width;
                padding = Mathf.FloorToInt(p / (textureCount - 1));
                width = w;
                height = frameHeight;
            }

            frames = new int[textureCount * 4];
            bx = 0;
            by = 0;
            atlasTexture = new Texture2D(width, height, TextureFormat.ARGB32, false);
            //atlasTexture.Apply();
            this.padding = padding;
        }

        public void AddFrame(Texture2D texture)
        {
            if (completeFrameCount == framesCount)
            {
                Debug.LogWarning("The atlas is completed, ne more texture can be added!");
                return;
            }
            if (texture.width != frameWidth || texture.height != frameHeight)
            {
                Debug.LogError($"The texture size [{texture.width},{texture.height}] is different from the expected [{frameWidth},{frameHeight}]");
                return;
            }

            _AddFrame(texture);
        }
        public void AddFrame(Color32[] pixelColors)
        {
            if (completeFrameCount == framesCount)
            {
                Debug.LogWarning("The atlas is completed, ne more pixel colors can be added!");
                return;
            }

            _AddFrame(pixelColors);
        }
        void _AddFrame(object frame)
        {
            int k = completeFrameCount * 4;
            frames[k] = bx;
            frames[k + 1] = by;

            switch (frame)
            {
                case Color32[]:
                    var colors = frame as Color32[];
                    atlasTexture.SetPixels32(bx, by, frameWidth, frameHeight, colors, 0);
                    break;
                case Texture2D:
                    Graphics.CopyTexture(frame as Texture2D, 0, 0, 0, 0, frameWidth, frameHeight, atlasTexture, 0, 0, bx, by);
                    break;
                default:
                    throw new Exception($"Invalid frame type <{frame.GetType()}> only Color32[] or Texture2D allowed.");
            }

            bx += frameWidth;
            frames[k + 2] = bx;
            frames[k + 3] = by + frameHeight;

            k += 4;
            bx += padding;
            if (width - bx < frameWidth)
            {
                bx = 0;
                by += frameHeight + padding;
            }

            completeFrameCount++;
            if (completeFrameCount == framesCount)
            {
                GenerateFrameUVFromTBVertices();
                atlasTexture.Apply();
            }
        }

        public Sprite CreateSprite(int frameIndex, Vector2 pivot, float pixelPerUnit = 100f)
        {
            int index = Mathf.Clamp(frameIndex, 0, framesCount - 1) * 4;
            return Sprite.Create(atlasTexture, new Rect((float)frames[index], (float)frames[index + 1], frameWidth, frameHeight), pivot, pixelPerUnit);
        }

        public Sprite CreateSprite(int frameIndex, SpriteAlignment pivotPosition = SpriteAlignment.Center, float pixelPerUnit = 100f)
        {
            Vector2 pivot = new Vector2(0.5f, 0.5f);
            switch (pivotPosition)
            {
                case SpriteAlignment.TopCenter:
                    pivot.y = 0;
                    break;
                case SpriteAlignment.BottomCenter:
                    pivot.y = 1;
                    break;
                case SpriteAlignment.BottomLeft:
                    pivot.y = 1;
                    pivot.x = 0;
                    break;
                case SpriteAlignment.BottomRight:
                    pivot.y = 1;
                    pivot.x = 1;
                    break;
                case SpriteAlignment.TopLeft:
                    pivot.x = 0;
                    pivot.y = 0;
                    break;
                case SpriteAlignment.TopRight:
                    pivot.x = 1;
                    pivot.y = 0;
                    break;
                case SpriteAlignment.LeftCenter:
                    pivot.x = 0;
                    pivot.y = 0.5f;
                    break;
                case SpriteAlignment.RightCenter:
                    pivot.x = 1;
                    pivot.y = 0.5f;
                    break;

            }
            return CreateSprite(frameIndex, pivot, pixelPerUnit);
        }

    }
}