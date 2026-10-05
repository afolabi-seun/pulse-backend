namespace Pulse.Application.Common;

public static class EmailTemplate
{
    /// <summary>
    /// Wraps a body fragment in the standard Pulse email shell.
    /// </summary>
    public static string Layout(string bodyHtml) =>
        $"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="UTF-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1.0" />
        </head>
        <body style="margin:0;padding:0;background:#f3f4f6;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;">
          <table width="100%" cellpadding="0" cellspacing="0" role="presentation" style="background:#f3f4f6;">
            <tr>
              <td align="center" style="padding:40px 16px;">
                <table width="600" cellpadding="0" cellspacing="0" role="presentation" style="max-width:600px;width:100%;background:#ffffff;border-radius:8px;overflow:hidden;">
                  <tr>
                    <td style="background:#2563eb;padding:24px 40px;">
                      <span style="color:#ffffff;font-size:22px;font-weight:700;letter-spacing:-0.5px;">Pulse</span>
                    </td>
                  </tr>
                  <tr>
                    <td style="padding:40px;color:#111827;font-size:15px;line-height:1.6;">
                      {bodyHtml}
                    </td>
                  </tr>
                  <tr>
                    <td style="padding:24px 40px;background:#f9fafb;border-top:1px solid #e5e7eb;">
                      <p style="margin:0;color:#9ca3af;font-size:12px;line-height:1.5;">
                        You're receiving this email because you have an account on Pulse.<br />
                        This is an automated message — please do not reply to this email.
                      </p>
                      <p style="margin:8px 0 0;color:#9ca3af;font-size:12px;">
                        &copy; Pulse &mdash; R&amp;D Team
                      </p>
                    </td>
                  </tr>
                </table>
              </td>
            </tr>
          </table>
        </body>
        </html>
        """;

    public static string Button(string href, string label) =>
        $"""
        <p style="margin:24px 0;">
          <a href="{href}" style="background:#2563eb;color:#ffffff;padding:12px 24px;border-radius:6px;text-decoration:none;font-weight:600;display:inline-block;">{label}</a>
        </p>
        """;

    public static string Muted(string text) =>
        $"""<p style="color:#6b7280;font-size:13px;">{text}</p>""";
}
