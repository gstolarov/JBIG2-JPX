using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Configuration;
using System.Runtime.Remoting.Metadata.W3cXsd2001;
using System.Windows.Forms;
using XPdf;

namespace wf {
	public partial class Form1 : Form {
		void loadFiles(string[] nms) {
			DateTime c = DateTime.Now;
			int err = 0;
			var dir = new FileInfo(nms[0]).DirectoryName + @"\out";
			Directory.CreateDirectory(dir);
			foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*.jpg"))
				f.Delete();
            foreach (var nm in nms) {
				FileInfo cf = new FileInfo(nm);
				try {
					var img = loadFile(nm);
					if (img != null) 
						img.Save(dir + @"\" + cf.Name + ".jpg", ImageFormat.Jpeg);
				}
				catch(Exception ex) {
					Console.WriteLine("Error processing " + nm);
					err++;
				}
			}
			Console.WriteLine("TooK:" + (DateTime.Now - c) + " Files " + err + "/" + nms.Length);
			MessageBox.Show("done " + err + " errors");
		}
		Image loadFile(string nm) {
			Application.DoEvents();
			string fn = nm.Substring(0, nm.Length - 3);
			byte[] data = File.ReadAllBytes(nm);
			byte[] glob = (File.Exists(fn + "glob"))
							? File.ReadAllBytes(fn + "glob") : null;
			Image img;
			DateTime c = DateTime.Now;
			if (!nm.ToLower().EndsWith("jb2"))
				img = new XPdfJpx(data).decodeImage();
			else
				//img = new XPdfJBig2(data, glob).decodeImage();
				img = new PBoxJBig2(data, glob).decodeImage();
			
			Console.WriteLine("" + (DateTime.Now - c) + " File " + nm);
			if (img != null)
				pictureBox1.Image = img;
			Application.DoEvents();
			return img;

		}
		public Form1() {
			InitializeComponent();
			try {
				//loadFile(@"C:\temp\pdfjs\jb2\042_3.jb2");
				loadFile(@"C:\temp\pdfjs\jb2\042_11.jb2");

				//loadFile(@"C:\temp\pdfjs\j2k\sm6.j2k");
			}
			catch (Exception ex) {
				MessageBox.Show("Error to load " + ex.Message);
			}
		}

		private void button1_Click(object sender, EventArgs e) {
			if (openFileDialog1.ShowDialog() == DialogResult.OK) {
				try {
					if (openFileDialog1.FileNames.Length > 1)
						loadFiles(openFileDialog1.FileNames);
					else
						pictureBox1.Image = loadFile(openFileDialog1.FileName);
				}
				catch (Exception ex) {
					MessageBox.Show("Error to load " + ex.Message);
				}
			}
		}
	}
}
