import pathlib, sys
def load(p):
    d = pathlib.Path(p).read_bytes()
    bom = d.startswith(b'\xef\xbb\xbf')
    t = d.decode('utf-8-sig')
    crlf = '\r\n' in t
    return t.replace('\r\n', '\n'), bom, crlf
def save(p, t, bom, crlf):
    if crlf: t = t.replace('\n', '\r\n')
    d = t.encode('utf-8')
    if bom: d = b'\xef\xbb\xbf' + d
    pathlib.Path(p).write_bytes(d)

# --- XAML 回滚 ---
p = r'SteamEyaWinUI\Pages\PersonalizationPage.xaml'
t, bom, crlf = load(p)
start = t.index('            <!-- 标签：个性化 / 轻松音乐')
marker = '''            <!-- 个性化 -->
            <StackPanel x:Name="ProfilePanel" Spacing="12">
'''
end = t.index('            <InfoBar x:Name="PageInfoBar" IsClosable="True" IsOpen="False" />')
t = t[:start] + marker_rel if False else t[:start] + t[end:]
# 删掉上面那个包裹标签与关闭标签
t = t.replace('''            <StackPanel x:Name="ProfilePanel" Spacing="12">
''', '', 1)
close_marker = '''            <InfoBar x:Name="PageInfoBar" IsClosable="True" IsOpen="False" />
            </StackPanel>

            <!-- 轻松音乐：内嵌官方网页，平台可切换（现在只开汽水音乐，其余留位） -->
'''
if close_marker not in t: sys.exit('CLOSE MARKER NOT FOUND')
tail_start = t.index(close_marker) + len('            <InfoBar x:Name="PageInfoBar" IsClosable="True" IsOpen="False" />\n')
t = t[:tail_start] + '            </StackPanel>\n        </StackPanel>' + t[t.index('        </StackPanel>', tail_start) + len('        </StackPanel>'):]
save(p, t, bom, crlf)
print('OK', p)