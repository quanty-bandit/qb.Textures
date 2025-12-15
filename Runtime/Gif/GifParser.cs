using System;
using UnityEngine;
using ThreeDISevenZeroR.UnityGifDecoder;
using System.Collections.Generic;
using ThreeDISevenZeroR.UnityGifDecoder.Model;

namespace qb.Gif
{
    public class GifParser
    {
        /// <summary>
        /// Check if a byte buffer is a gif formatted image
        /// </summary>
        /// <param name="buffer">The input byte buffer to test</param>
        /// <returns>true if the buffer contain a gif image signature</returns>
        public static bool IsGif(byte[] buffer)
        {
            // Signature(3 Bytes)
            // 0x47 0x49 0x46 (GIF)
            if (buffer[0] != 'G' || buffer[1] != 'I' || buffer[2] != 'F')
            {
                return false;
            }
            // Version(3 Bytes)
            // 0x38 0x37 0x61 (87a) or 0x38 0x39 0x61 (89a)
            if ((buffer[3] != '8' || buffer[4] != '7' || buffer[5] != 'a') &&
                (buffer[3] != '8' || buffer[4] != '9' || buffer[5] != 'a'))
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Get gif images from gif formatted input buffer.
        /// Update the delays in case of
        /// </summary>
        /// <param name="inputBuffer">The gif formatted bytes input bufer</param>
        /// <param name="maxImageCount">
        /// The max image count for reduction if needed
        /// The default value set to -1 means that the sequence will be return with the original 
        /// gif frame count
        /// </param>
        /// <returns>The </returns>        
        public static List<GifImage> GetImages(byte[] inputBuffer, out int imageWidth, out int imageHeight, int maxImageCount = -1)
        {
            List<GifImage> images = new List<GifImage>();
            using (var gifStream = new GifStream(inputBuffer))
            {
                while (gifStream.HasMoreData)
                {
                    if (gifStream.CurrentToken == GifStream.Token.Image)
                    {
                        var img = gifStream.ReadImage();
                        var colors = img.colors;
                        int cc = colors.Length;
                        Color32[] nc = new Color32[cc];
                        Array.Copy(colors, nc, cc);
                        img.colors = nc;
                        images.Add(img);
                    }
                    else
                        gifStream.SkipToken();
                }
                imageWidth = gifStream.Header.width;
                imageHeight = gifStream.Header.height;
            }
            if (maxImageCount > 0 && images.Count > maxImageCount)
            {
                int imageCount = images.Count;
                int count = Mathf.Min(imageCount, maxImageCount);
                float step = (imageCount - 2) / (float)(maxImageCount);
                int k = 0;
                int j = 1;

                List<GifImage> tmpImages = new List<GifImage>() { images[0] };

                int totalDelay = 0;

                int minDelay = 12;
                for (int index = 0; index < imageCount; index++)
                {
                    if (k >= step)
                    {
                        var image = images[index];
                        image.delay += (totalDelay <= minDelay ? totalDelay : minDelay);
                        tmpImages.Add(image);
                        k = 0;
                        j++;
                        totalDelay = 0;
                        if (j >= count)
                            break;
                    }
                    else
                    {
                        totalDelay += images[index].delay;
                    }
                    k++;
                }
                tmpImages[tmpImages.Count - 1] = images[imageCount - 1];
                images = tmpImages;
            }

            return images;
        }

    }
}
