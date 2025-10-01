## JPX/JBIG2 image decoder

While working with PDF compression/OCR ([https://github.com/gstolarov/pdf-shrink](https://github.com/gstolarov/pdf-shrink)) I found out that some image files (JPX2000 and JBIG2) are not being handled by iTextSharp library I used. Unfortunately options to decode those files in C# are very limited and one that are available don't properly hand most of the files I had at hand. 

### JPX (JPEG 2000)

Those files (commonly using .j2k extension) are optimized version of the JPEG files to store full color images. They provide somewhat better compression ratio. Nowadays OpenJPEG library is providing functionality to decode them. I found no free C# code that I can use. Since I use PDF functionality in the multiple projects, I didn't want to create dependency on external native DLL so using OpenJPEG was less then ideal. Other then OpenJPEG library, the only other open source I found dealing with JPX files was XPDF (c++) code ([https://github.com/kermitt2/xpdf-4.00/blob/master/xpdf/JPXStream.cc](https://github.com/kermitt2/xpdf-4.00/blob/master/xpdf/JPXStream.cc)). I migrated and heavily refactored this code to suit my needs.

The resulting code does seem to do the job for all the files I had. The only problem I found is dealing with greyscale files. When stored in PDF those JPX files seems to utilize PDF color space for palette so function to extract images has a palette parameter. When provided it will be utilized, otherwise it will be just black and white. The decodeImage function can be easily modified to use grayscale instead.

To convert JPX files to images use following code:

```plaintext
Image img = new XPdfJpx(byteArray).decodeImage()
```

### JBIG2

JBIG2 file type was designed to store monochrome images. Actually there is a number of decoders available to handle those files even in c#:

*   [https://github.com/Daniel-Glucksman/JBig2Decoder.NET](https://github.com/Daniel-Glucksman/JBig2Decoder.NET)
*   [https://github.com/afila/JBIG2-Image-Decoder.NET](https://github.com/afila/JBIG2-Image-Decoder.NET)
*   [https://github.com/nicholsab/JBig2Decoder.NETStandard](https://github.com/nicholsab/JBig2Decoder.NETStandard)

Unfortunately all of those failed to handle most of the files I was testing with. My next step was to try to utilize xPdf ([https://github.com/kermitt2/xpdf-4.00/blob/master/xpdf/JBIG2Stream.cc](https://github.com/kermitt2/xpdf-4.00/blob/master/xpdf/JBIG2Stream.cc)). This one was working much better but still was failing with a lot of files. The final solution was to convert PdfBox addon ([https://github.com/apache/pdfbox-jbig2](https://github.com/apache/pdfbox-jbig2)). This one while still fails to handle some files but works significantly better then other options.

To convert JBIG files to images use following code:

```plaintext
Image img = new PBoxJBig2(byteData, globBytes).decodeImage();
```

The second optional parameter is global symbols sometimes used in PDF for the JBIG files.

### Test application

In the upper right corner of the test application is small folder icon. Once clicked it will prompt for file(s) to open. If one file selected it will open it in the window. If multiple files are selected, it will create a subfolder **out** in the directory where the files are located, _delete all the **JPG** files from there_ and create a new **JPG** file for each file selected. 

The application assumes that J2K files are JPX/JPEG2000, and JB2 files are JBIG. For each JB2 file it will check faile with the same name and .GLOB extension. If this file is found it will be assumed to contain global symbox for GB2 file.

Test images I used are stored in the testImgs.zip

If you planning to use code in your project just include XPdfImg.cs and have a blast.