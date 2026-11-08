namespace App;

partial class Form1
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    private void InitializeComponent()
    {
        this.components = new System.ComponentModel.Container();
        this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Dpi;
        this.ClientSize = new System.Drawing.Size(1200, 750);
        this.MinimumSize = new System.Drawing.Size(800, 500);
        this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
        this.Name = "Form1";
        this.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
        this.Text = "YouTube Music";
        this.BackColor = System.Drawing.Color.FromArgb(3, 3, 3);
        this.Opacity = 0;
    }

    #endregion
}
