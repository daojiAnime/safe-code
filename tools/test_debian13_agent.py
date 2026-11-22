#!/usr/bin/env python3
"""验证 Debian 13 (Trixie) 系统识别、/etc/locale.gen 生成与 /etc/locale.conf 读取。"""
import contextlib,importlib.util,io,json,subprocess,tempfile,unittest
import xml.etree.ElementTree as ET
from pathlib import Path
from unittest.mock import patch

root=Path(__file__).resolve().parent.parent
spec=importlib.util.spec_from_file_location('agent',root/'src/SafeCode.Core/Remote/agent.py')
agent=importlib.util.module_from_spec(spec);spec.loader.exec_module(agent)

class Debian13Tests(unittest.TestCase):
    def test_os_detection_accepts_debian_13_and_rejects_others(self):
        self.assertTrue(agent.is_debian_13({'ID':'debian','VERSION_ID':'13','PRETTY_NAME':'Debian GNU/Linux 13 (trixie)'}))
        self.assertTrue(agent.is_debian_13({'ID':'debian','VERSION_ID':'13.1'}))
        self.assertTrue(agent.is_debian_13({'ID':'debian','VERSION_CODENAME':'trixie'}))
        self.assertFalse(agent.is_debian_13({'ID':'debian','VERSION_ID':'12','VERSION_CODENAME':'bookworm'}))
        self.assertFalse(agent.is_debian_13({'ID':'debian'}))
        self.assertFalse(agent.is_debian_13({'ID':'ubuntu','VERSION_ID':'24.04'}))

    def test_locale_packages_do_not_require_ubuntu_language_packs(self):
        for loc in ['en_US.UTF-8','ja_JP.UTF-8','zh_CN.UTF-8']:
            pkgs=agent.locale_packages(loc)
            self.assertEqual(pkgs,['locales','tzdata','fonts-noto-cjk'])
            self.assertFalse(any(p.startswith('language-pack-') for p in pkgs))
        self.assertEqual(agent.locale_packages('en_US.UTF-8','Asia/Seoul'),['locales','tzdata','fonts-noto-cjk','fonts-nanum'])

    def test_debian13_locale_gen_and_locale_conf(self):
        with tempfile.TemporaryDirectory() as tmp:
            base=Path(tmp)
            (base/'usr/share/zoneinfo/Asia').mkdir(parents=True)
            (base/'usr/share/zoneinfo/Asia/Tokyo').write_text('TZif')
            (base/'usr/share/i18n').mkdir(parents=True)
            (base/'usr/share/i18n/SUPPORTED').write_text('en_US.UTF-8 UTF-8\nja_JP.UTF-8 UTF-8\n')
            (base/'etc').mkdir(parents=True)
            (base/'etc/locale.gen').write_text('# en_US.UTF-8 UTF-8\n# ja_JP.UTF-8 UTF-8\n')
            (base/'etc/locale.conf').write_text('LANG=ja_JP.UTF-8\n')
            state=base/'state';state.mkdir()
            streamed=[];ran=[]
            def local_path(value):
                s=str(value)
                return base/s.lstrip('/') if s.startswith(('/usr/','/etc/')) else Path(value)
            def fake_run(args,check=True,env=None,stdin=None):
                ran.append(args)
                if args==['locale','-a']:return subprocess.CompletedProcess(args,0,'C\nC.utf8\nja_JP.utf8\n','')
                return subprocess.CompletedProcess(args,0,'','')
            with patch.object(agent,'Path',local_path),patch.object(agent,'STATE',state),patch.object(agent,'account',return_value={'username':'workspace'}),patch.object(agent,'home',return_value=base/'home/workspace'),patch.object(agent,'user_files'),patch.object(agent,'apt'),patch.object(agent,'log'),patch.object(agent,'ensure_fish'),patch.object(agent,'setup_xsession'),patch.object(agent,'stream',side_effect=lambda a,env=None:streamed.append(a)),patch.object(agent,'run',side_effect=fake_run):
                agent.setup_locale({'timezone':'Asia/Tokyo','locale':'ja_JP.UTF-8'})
                self.assertIn('\nja_JP.UTF-8 UTF-8\n','\n'+(base/'etc/locale.gen').read_text())
                self.assertEqual(streamed,[['locale-gen']])
                self.assertEqual(agent.read_system_locale(),'ja_JP.UTF-8')
                self.assertTrue(agent.locale_generated('ja_JP.UTF-8'))

    def test_ensure_fish_sets_login_shell_and_fish_conf(self):
        written={}
        ran=[]
        class FakePwd:
            pw_shell='/bin/bash'
            pw_dir='/home/workspace'
        with tempfile.TemporaryDirectory() as tmp:
            state=Path(tmp)
            (state/'localization.json').write_text(json.dumps({'timezone':'Asia/Seoul','locale':'en_US.UTF-8'}))
            with patch.object(agent,'STATE',state), \
                 patch.object(agent,'apt'), \
                 patch.object(agent,'resolve_fish',return_value='/usr/bin/fish'), \
                 patch.object(agent,'read_config',return_value={'username':'workspace'}), \
                 patch.object(agent.pwd,'getpwnam',return_value=FakePwd()), \
                 patch.object(agent,'Path',side_effect=lambda p: state if p is state else (Path(__file__) if str(p)=='/usr/bin/fish' else Path(p))), \
                 patch.object(agent,'run',side_effect=lambda args,*a,**k:(ran.append(args),subprocess.CompletedProcess(args,0,'',''))[1]), \
                 patch.object(agent,'user_write',side_effect=lambda rel,content,mode=0o644:written.setdefault(rel,(content,mode))):
                fish_bin=agent.ensure_fish()
                self.assertEqual(fish_bin,'/usr/bin/fish')
                self.assertIn(['usermod','-s','/usr/bin/fish','workspace'],ran)
                self.assertIn('.config/fish/conf.d/safe-code.fish',written)
                conf=written['.config/fish/conf.d/safe-code.fish'][0]
                self.assertIn('set -gx LANG en_US.UTF-8',conf)
                self.assertIn('fish_add_path -g -p "$HOME/.local/bin" /opt/safe-code/node/bin',conf)

    def test_setup_ssh_configures_herdr_with_fish_and_tmux_fallback(self):
        written={}
        appended=[]
        def fake_user_write(rel,content,mode=0o644):
            written[rel]=(content,mode)
        def fake_user_files(op,rel,**kwargs):
            if op=='append_unique':appended.append((rel,kwargs.get('content')))
        with patch.object(agent,'account',return_value={'username':'workspace'}), \
             patch.object(agent,'home',return_value=Path('/home/workspace')), \
             patch.object(agent,'ensure_fish',return_value='/usr/bin/fish'), \
             patch.object(agent,'version',return_value='herdr 0.9.5'), \
             patch.object(agent,'user_read',return_value=''), \
             patch.object(agent,'user_write',side_effect=fake_user_write), \
             patch.object(agent,'user_files',side_effect=fake_user_files), \
             patch.object(agent,'run',return_value=subprocess.CompletedProcess([],0,'','')), \
             patch.object(agent,'log'):
            pub='ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABAQC7 safe-code-0123456789abcdef'
            agent.setup_ssh({'publicKey':pub})
            self.assertIn('.config/herdr/config.toml',written)
            self.assertIn('default_shell = "/usr/bin/fish"',written['.config/herdr/config.toml'][0])
            self.assertIn('shell_mode = "login"',written['.config/herdr/config.toml'][0])
            self.assertIn('.local/bin/safe-code-terminal',written)
            script=written['.local/bin/safe-code-terminal'][0]
            self.assertIn('export SHELL=/usr/bin/fish',script)
            self.assertIn('exec "$HOME/.local/bin/herdr" --session safe-code',script)
            self.assertIn('exec tmux new-session -A -s safe-code',script)
            self.assertTrue(any(r=='.ssh/authorized_keys' for r,_ in appended))

    @staticmethod
    def _parse_xfconf(xml_text):
        root=ET.fromstring(xml_text)
        props={}
        def walk(node,prefix=''):
            for child in node.findall('property'):
                name=child.attrib['name']
                path=f'{prefix}/{name}' if prefix else name
                typ=child.attrib.get('type','')
                if typ=='empty':
                    walk(child,path)
                else:
                    props[path]=(typ,child.attrib.get('value'))
        walk(root)
        return root,props

    def test_read_desktop_scale_defaults_and_persistence(self):
        with tempfile.TemporaryDirectory() as tmp:
            state=Path(tmp)
            with patch.object(agent,'STATE',state):
                self.assertEqual(agent.read_desktop_scale(),100)
                for valid in (100,150,200):
                    (state/'desktop-scale.json').write_text(json.dumps({'scale':valid}))
                    self.assertEqual(agent.read_desktop_scale(),valid)
                for raw in ('not-json',json.dumps([]),json.dumps({}),json.dumps({'scale':125}),json.dumps({'scale':'150'}),json.dumps({'scale':True})):
                    (state/'desktop-scale.json').write_text(raw)
                    self.assertEqual(agent.read_desktop_scale(),100)

    def test_apply_desktop_scale_invalid_values_raise(self):
        with tempfile.TemporaryDirectory() as tmp:
            state=Path(tmp)
            with patch.object(agent,'STATE',state),patch.object(agent,'account',return_value={'username':'workspace'}):
                for bad in (0,50,125,175,300,-100,'150',None,True,150.0):
                    with self.assertRaises(RuntimeError) as ctx:
                        agent.apply_desktop_scale(bad,live=False)
                    self.assertIn('100',str(ctx.exception))
                    self.assertIn('150',str(ctx.exception))
                    self.assertIn('200',str(ctx.exception))
                self.assertFalse((state/'desktop-scale.json').exists())

    def test_apply_desktop_scale_100_150_200_configs(self):
        cases={
            100:{
                'window_scaling':'1',
                'xft_dpi':'96',
                'cursor':'24',
                'theme':'Default',
                'xrdb_dpi':'96',
                'qt_scale':'1',
                'gdk_scale':'1',
                'chrome_exec':'Exec=google-chrome-stable',
            },
            150:{
                'window_scaling':'1',
                'xft_dpi':'144',
                'cursor':'36',
                'theme':'Default-hdpi',
                'xrdb_dpi':'144',
                'qt_scale':'1.5',
                'gdk_scale':'1',
                'chrome_exec':'Exec=google-chrome-stable --force-device-scale-factor=1.5',
            },
            200:{
                'window_scaling':'2',
                'xft_dpi':'96',
                'cursor':'48',
                'theme':'Default-xhdpi',
                'xrdb_dpi':'96',
                'qt_scale':'2',
                'gdk_scale':'2',
                'chrome_exec':'Exec=google-chrome-stable --force-device-scale-factor=2',
            },
        }
        for scale,expected in cases.items():
            with self.subTest(scale=scale),tempfile.TemporaryDirectory() as tmp:
                base=Path(tmp)
                state=base/'state';state.mkdir()
                (state/'localization.json').write_text(json.dumps({'timezone':'Asia/Tokyo','locale':'ja_JP.UTF-8'}))
                home_dir=base/'home/workspace'
                (home_dir/'Desktop').mkdir(parents=True)
                (home_dir/'Desktop/Chrome.desktop').write_text('[Desktop Entry]\n')
                for theme in ('Default','Default-hdpi','Default-xhdpi'):
                    (base/'usr/share/themes'/theme).mkdir(parents=True)
                written={}
                def local_path(value):
                    s=str(value)
                    return base/s.lstrip('/') if s.startswith('/usr/share/themes') else Path(value)
                def fake_user_write(rel,content,mode=0o644):
                    written[rel]=(content,mode)
                with patch.object(agent,'STATE',state), \
                     patch.object(agent,'Path',local_path), \
                     patch.object(agent,'account',return_value={'username':'workspace'}), \
                     patch.object(agent,'home',return_value=home_dir), \
                     patch.object(agent,'user_write',side_effect=fake_user_write), \
                     patch.object(agent,'inspect',return_value={'desktopScale':scale}), \
                     patch.object(agent,'log'):
                    res=agent.apply_desktop_scale(scale,live=False)

                self.assertEqual(res,{'scale':scale,'appliedLive':False,'server':{'desktopScale':scale}})
                self.assertEqual(json.loads((state/'desktop-scale.json').read_text()),{'scale':scale})
                with patch.object(agent,'STATE',state):
                    self.assertEqual(agent.read_desktop_scale(),scale)

                xsettings_xml,_=written['.config/xfce4/xfconf/xfce-perchannel-xml/xsettings.xml']
                xroot,xprops=self._parse_xfconf(xsettings_xml)
                self.assertEqual(xroot.tag,'channel')
                self.assertEqual(xroot.attrib.get('name'),'xsettings')
                self.assertEqual(xprops['Gdk/WindowScalingFactor'],('int',expected['window_scaling']))
                self.assertEqual(xprops['Xft/DPI'],('int',expected['xft_dpi']))
                self.assertEqual(xprops['Gtk/CursorThemeSize'],('int',expected['cursor']))
                self.assertEqual(xprops['Xft/Antialias'],('int','1'))
                self.assertEqual(xprops['Xft/Hinting'],('int','1'))
                self.assertEqual(xprops['Xft/HintStyle'],('string','hintslight'))
                self.assertEqual(xprops['Xft/RGBA'],('string','rgb'))
                if scale==200:
                    # 200% must never combine WindowScalingFactor=2 with Xft/DPI=192 (4x GTK3 double-scale).
                    self.assertNotEqual(xprops['Xft/DPI'][1],'192')

                xfwm4_xml,_=written['.config/xfce4/xfconf/xfce-perchannel-xml/xfwm4.xml']
                wroot,wprops=self._parse_xfconf(xfwm4_xml)
                self.assertEqual(wroot.tag,'channel')
                self.assertEqual(wroot.attrib.get('name'),'xfwm4')
                self.assertEqual(wprops['general/use_compositing'],('bool','false'))
                self.assertEqual(wprops['general/theme'],('string',expected['theme']))

                xresources,_=written['.Xresources']
                self.assertIn(f"Xft.dpi: {expected['xrdb_dpi']}",xresources.splitlines())
                self.assertIn(f"Xcursor.size: {expected['cursor']}",xresources.splitlines())

                xsession,xsession_mode=written['.xsession']
                self.assertEqual(xsession_mode,0o755)
                xrdb_line='[ -f "$HOME/.Xresources" ] && xrdb -merge "$HOME/.Xresources"'
                self.assertIn(xrdb_line,xsession)
                self.assertLess(xsession.index(xrdb_line),xsession.index('exec startxfce4'))

                xsessionrc,_=written['.xsessionrc']
                rc_lines=xsessionrc.splitlines()
                self.assertIn('export LANG=ja_JP.UTF-8',rc_lines)
                self.assertIn('export QT_AUTO_SCREEN_SCALE_FACTOR=0',rc_lines)
                self.assertIn(f"export QT_SCALE_FACTOR={expected['qt_scale']}",rc_lines)
                self.assertIn(f"export XCURSOR_SIZE={expected['cursor']}",rc_lines)
                if scale==200:
                    self.assertIn('export GDK_SCALE=2',rc_lines)
                else:
                    self.assertNotIn('export GDK_SCALE=2',rc_lines)

                chrome_desktop,chrome_mode=written['Desktop/Chrome.desktop']
                self.assertEqual(chrome_mode,0o755)
                self.assertIn(expected['chrome_exec'],chrome_desktop.splitlines())

    def test_apply_desktop_scale_skips_chrome_desktop_when_absent(self):
        with tempfile.TemporaryDirectory() as tmp:
            base=Path(tmp)
            state=base/'state';state.mkdir()
            home_dir=base/'home/workspace';home_dir.mkdir(parents=True)
            written={}
            with patch.object(agent,'STATE',state), \
                 patch.object(agent,'account',return_value={'username':'workspace'}), \
                 patch.object(agent,'home',return_value=home_dir), \
                 patch.object(agent,'user_write',side_effect=lambda rel,content,mode=0o644:written.setdefault(rel,(content,mode))), \
                 patch.object(agent,'inspect',return_value={'desktopScale':150}), \
                 patch.object(agent,'log'):
                agent.apply_desktop_scale(150,live=False)
                self.assertNotIn('Desktop/Chrome.desktop',written)
                agent.write_chrome_desktop(150)
                self.assertIn('Exec=google-chrome-stable --force-device-scale-factor=1.5',written['Desktop/Chrome.desktop'][0].splitlines())

    def test_xfwm4_theme_fallback_chain(self):
        scenarios=[
            (200,['Default-xhdpi','Default-hdpi'],'Default-xhdpi'),
            (200,['Default-hdpi'],'Default-hdpi'),
            (200,[],'Default'),
            (150,['Default-hdpi','Default-xhdpi'],'Default-hdpi'),
            (150,['Default-xhdpi'],'Default-xhdpi'),
            (150,[],'Default'),
            (100,['Default-xhdpi','Default-hdpi'],'Default'),
        ]
        for scale,existing,expected in scenarios:
            with self.subTest(scale=scale,existing=existing),tempfile.TemporaryDirectory() as tmp:
                base=Path(tmp)
                (base/'usr/share/themes').mkdir(parents=True)
                for name in existing:
                    (base/'usr/share/themes'/name).mkdir()
                def local_path(value):
                    s=str(value)
                    return base/s.lstrip('/') if s.startswith('/usr/share/themes') else Path(value)
                with patch.object(agent,'Path',local_path):
                    self.assertEqual(agent.resolve_xfwm4_theme(scale),expected)

    def test_apply_desktop_scale_live_refresh_and_failure_resilience(self):
        class FakePwd:
            pw_uid=1001
            pw_dir='/home/workspace'
        with tempfile.TemporaryDirectory() as tmp:
            base=Path(tmp)
            state=base/'state';state.mkdir()
            home_dir=base/'home/workspace';home_dir.mkdir(parents=True)
            (base/'usr/share/themes/Default-xhdpi').mkdir(parents=True)
            (base/'proc/4242').mkdir(parents=True)
            (base/'proc/4242/environ').write_bytes(b'USER=workspace\x00DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/1001/bus\x00')
            ran=[]
            def local_path(value):
                s=str(value)
                return base/s.lstrip('/') if s.startswith(('/usr/share/themes','/proc')) else Path(value)
            def fake_run(args,check=True,env=None,stdin=None,timeout=180):
                ran.append(args)
                if args[:4]==['pgrep','-u','1001','-x']:
                    return subprocess.CompletedProcess(args,0,'4242\n','')
                return subprocess.CompletedProcess(args,0,'','')
            with patch.object(agent,'STATE',state), \
                 patch.object(agent,'Path',local_path), \
                 patch.object(agent,'account',return_value={'username':'workspace'}), \
                 patch.object(agent,'home',return_value=home_dir), \
                 patch.object(agent.pwd,'getpwnam',return_value=FakePwd()), \
                 patch.object(agent,'user_write'), \
                 patch.object(agent,'run',side_effect=fake_run), \
                 patch.object(agent,'inspect',return_value={'desktopScale':200}), \
                 patch.object(agent,'log'):
                res=agent.apply_desktop_scale(200,live=True)
            self.assertTrue(res['appliedLive'])
            self.assertIn(['pgrep','-u','1001','-x','xfce4-session'],ran)
            xfconf_cmds=[cmd for cmd in ran if 'xfconf-query' in cmd]
            self.assertEqual(len(xfconf_cmds),8)
            for cmd in xfconf_cmds:
                self.assertIn('DISPLAY=:10.0',cmd)
                self.assertIn('DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/1001/bus',cmd)
            self.assertTrue(any('/Gdk/WindowScalingFactor' in cmd and cmd[-1]=='2' for cmd in xfconf_cmds))
            self.assertTrue(any('/Xft/DPI' in cmd and cmd[-1]=='96' for cmd in xfconf_cmds))
            self.assertTrue(any('/general/theme' in cmd and cmd[-1]=='Default-xhdpi' for cmd in xfconf_cmds))
            self.assertTrue(any('xrdb' in cmd and '-merge' in cmd and '/home/workspace/.Xresources' in cmd for cmd in ran))
            self.assertTrue(any('xfce4-panel -r' in ' '.join(cmd) and '</dev/null >/dev/null 2>&1' in ' '.join(cmd) for cmd in ran))

            # Failure during xfconf-query must not fail apply_desktop_scale; appliedLive becomes False.
            def failing_run(args,check=True,env=None,stdin=None,timeout=180):
                if args[:4]==['pgrep','-u','1001','-x']:
                    return subprocess.CompletedProcess(args,0,'4242\n','')
                return subprocess.CompletedProcess(args,1,'','dbus error')
            with patch.object(agent,'STATE',state), \
                 patch.object(agent,'Path',local_path), \
                 patch.object(agent,'account',return_value={'username':'workspace'}), \
                 patch.object(agent,'home',return_value=home_dir), \
                 patch.object(agent.pwd,'getpwnam',return_value=FakePwd()), \
                 patch.object(agent,'user_write'), \
                 patch.object(agent,'run',side_effect=failing_run), \
                 patch.object(agent,'inspect',return_value={'desktopScale':150}), \
                 patch.object(agent,'log'):
                res_fail=agent.apply_desktop_scale(150,live=True)
            self.assertEqual(res_fail['scale'],150)
            self.assertFalse(res_fail['appliedLive'])

    def test_inspect_exposes_desktop_scale(self):
        with tempfile.TemporaryDirectory() as tmp:
            base=Path(tmp)
            state=base/'state';state.mkdir()
            (base/'etc').mkdir(parents=True)
            (base/'etc/os-release').write_text('ID=debian\nVERSION_ID="13"\nPRETTY_NAME="Debian GNU/Linux 13 (trixie)"\n')
            (base/'proc').mkdir(parents=True)
            (base/'proc/meminfo').write_text('MemTotal:        4096000 kB\n')
            def local_path(value):
                s=str(value)
                return base/s.lstrip('/') if s.startswith(('/etc/','/proc/')) else Path(value)
            def fake_run(args,check=True,env=None,stdin=None,timeout=180):
                if args[:2]==['ip','-j']:
                    return subprocess.CompletedProcess(args,0,'[]','')
                return subprocess.CompletedProcess(args,0,'UTC\n','')
            with patch.object(agent,'STATE',state), \
                 patch.object(agent,'Path',local_path), \
                 patch.object(agent.os,'geteuid',return_value=0), \
                 patch.object(agent,'read_config',return_value=None), \
                 patch.object(agent,'run',side_effect=fake_run), \
                 patch.object(agent,'package_installed',return_value=False), \
                 patch.object(agent,'active',return_value=False), \
                 patch.object(agent,'version',return_value=''), \
                 patch.object(agent,'active_jobs',return_value=[]):
                self.assertEqual(agent.inspect()['desktopScale'],100)
                (state/'desktop-scale.json').write_text(json.dumps({'scale':150}))
                self.assertEqual(agent.inspect()['desktopScale'],150)
                (state/'desktop-scale.json').write_text(json.dumps({'scale':200}))
                self.assertEqual(agent.inspect()['desktopScale'],200)

    def test_setup_desktop_applies_scale_before_restarting_xrdp(self):
        with tempfile.TemporaryDirectory() as tmp:
            base=Path(tmp)
            state=base/'state';state.mkdir()
            (base/'etc/xrdp').mkdir(parents=True)
            (base/'etc/xrdp/xrdp.ini').write_text('[Globals]\nport=3389\n')
            (base/'etc/xrdp/sesman.ini').write_text('[Security]\nAllowRootLogin=true\n[Sessions]\nMaxSessions=50\nKillDisconnected=false\nDisconnectedTimeLimit=0\nIdleTimeLimit=0\n')
            events=[]
            def local_path(value):
                s=str(value)
                return base/s.lstrip('/') if s.startswith('/etc/xrdp/') else Path(value)
            def fake_run(args,check=True,env=None,stdin=None,timeout=180):
                events.append(('run',args))
                return subprocess.CompletedProcess(args,0,'','')
            def fake_apply(scale,live=True):
                events.append(('scale',scale,live))
                return {'scale':scale,'appliedLive':False,'server':{}}
            with patch.object(agent,'STATE',state), \
                 patch.object(agent,'Path',local_path), \
                 patch.object(agent,'account',return_value={'username':'workspace'}), \
                 patch.object(agent,'apt'), \
                 patch.object(agent,'run',side_effect=fake_run), \
                 patch.object(agent,'apply_desktop_scale',side_effect=fake_apply), \
                 patch.object(agent,'active',return_value=True), \
                 patch.object(agent,'log'):
                agent.setup_desktop({'desktopScale':150})
                scale_idx=events.index(('scale',150,False))
                restart_idx=events.index(('run',['systemctl','restart','xrdp']))
                self.assertLess(scale_idx,restart_idx)

                events.clear()
                (state/'desktop-scale.json').write_text(json.dumps({'scale':200}))
                agent.setup_desktop({'desktopScale':999})
                self.assertIn(('scale',200,False),events)

    def test_main_dispatches_desktop_scale_operation(self):
        with tempfile.TemporaryDirectory() as tmp:
            base=Path(tmp)
            state=base/'state';state.mkdir()
            req_path=base/'request.json'
            req_path.write_text(json.dumps({'op':'desktop_scale','options':{'scale':200}}))
            called=[]
            def fake_apply(scale,live=True):
                called.append((scale,live))
                return {'scale':scale,'appliedLive':True,'server':{'desktopScale':scale}}
            buf=io.StringIO()
            with patch.object(agent,'STATE',state), \
                 patch.object(agent,'require_debian_13'), \
                 patch.object(agent,'secure_root_directory'), \
                 patch.object(agent,'write'), \
                 patch.object(agent.os,'geteuid',return_value=0), \
                 patch.object(agent,'apply_desktop_scale',side_effect=fake_apply), \
                 patch.object(agent.sys,'argv',['agent.py',str(req_path)]), \
                 contextlib.redirect_stdout(buf):
                agent.main()
            self.assertEqual(called,[(200,True)])
            out=buf.getvalue().strip()
            self.assertTrue(out.startswith('SC_RESULT:'))
            self.assertEqual(json.loads(out[len('SC_RESULT:'):]),{'scale':200,'appliedLive':True,'server':{'desktopScale':200}})

            with patch.object(agent,'STATE',state), \
                 patch.object(agent,'require_debian_13'), \
                 patch.object(agent.os,'geteuid',return_value=1000), \
                 patch.object(agent.sys,'argv',['agent.py',str(req_path)]):
                with self.assertRaises(RuntimeError):
                    agent.main()

    def test_merge_xfconf_xml_preserves_existing_xml11_properties(self):
        existing=(
            '<?xml version="1.1" encoding="UTF-8"?>\n'
            '<channel name="xsettings" version="1.0">\n'
            '  <property name="Gtk" type="empty">\n'
            '    <property name="DecorationLayout" type="string" value="icon,menu:minimize,maximize,close"/>\n'
            '    <property name="CursorThemeSize" type="empty"/>\n'
            '  </property>\n'
            '  <property name="Gdk" type="empty">\n'
            '    <property name="WindowScalingFactor" type="int" value="1"/>\n'
            '  </property>\n'
            '</channel>\n'
        )
        merged=agent._merge_xfconf_xml(existing,'xsettings',[
            (('Gtk','CursorThemeSize'),'int',48),
            (('Gdk','WindowScalingFactor'),'int',2),
            (('Xft','Antialias'),'int',1),
        ])
        root=ET.fromstring(merged)
        self.assertEqual(root.find("./property[@name='Gtk']/property[@name='DecorationLayout']").get('value'),'icon,menu:minimize,maximize,close')
        self.assertEqual(root.find("./property[@name='Gtk']/property[@name='CursorThemeSize']").get('value'),'48')
        self.assertEqual(root.find("./property[@name='Gdk']/property[@name='WindowScalingFactor']").get('value'),'2')
        self.assertEqual(root.find("./property[@name='Xft']/property[@name='Antialias']").get('value'),'1')

if __name__=='__main__':
    unittest.main()
