#!/usr/bin/env python3
"""验证 Debian 13 (Trixie) 系统识别、/etc/locale.gen 生成与 /etc/locale.conf 读取。"""
import importlib.util,json,subprocess,tempfile,unittest
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
            self.assertEqual(pkgs,['locales','tzdata'])
            self.assertFalse(any(p.startswith('language-pack-') for p in pkgs))

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
            with patch.object(agent,'Path',local_path),patch.object(agent,'STATE',state),patch.object(agent,'account',return_value={'username':'workspace'}),patch.object(agent,'apt'),patch.object(agent,'log'),patch.object(agent,'setup_xsession'),patch.object(agent,'stream',side_effect=lambda a,env=None:streamed.append(a)),patch.object(agent,'run',side_effect=fake_run):
                agent.setup_locale({'timezone':'Asia/Tokyo','locale':'ja_JP.UTF-8'})
                self.assertIn('\nja_JP.UTF-8 UTF-8\n','\n'+(base/'etc/locale.gen').read_text())
                self.assertEqual(streamed,[['locale-gen']])
                self.assertEqual(agent.read_system_locale(),'ja_JP.UTF-8')
                self.assertTrue(agent.locale_generated('ja_JP.UTF-8'))

    def test_setup_ssh_configures_herdr_with_tmux_fallback(self):
        written={}
        appended=[]
        def fake_user_write(rel,content,mode=0o644):
            written[rel]=(content,mode)
        def fake_user_files(op,rel,**kwargs):
            if op=='append_unique':appended.append((rel,kwargs.get('content')))
        with patch.object(agent,'account',return_value={'username':'workspace'}), \
             patch.object(agent,'home',return_value=Path('/home/workspace')), \
             patch.object(agent,'version',return_value='herdr 0.9.5'), \
             patch.object(agent,'user_read',return_value=''), \
             patch.object(agent,'user_write',side_effect=fake_user_write), \
             patch.object(agent,'user_files',side_effect=fake_user_files), \
             patch.object(agent,'log'):
            pub='ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABAQC7 safe-code-0123456789abcdef'
            agent.setup_ssh({'publicKey':pub})
            self.assertIn('.config/herdr/config.toml',written)
            self.assertIn('shell_mode = "login"',written['.config/herdr/config.toml'][0])
            self.assertIn('.local/bin/safe-code-terminal',written)
            script=written['.local/bin/safe-code-terminal'][0]
            self.assertIn('exec "$HOME/.local/bin/herdr" --session safe-code',script)
            self.assertIn('exec tmux new-session -A -s safe-code',script)
            self.assertTrue(any(r=='.ssh/authorized_keys' for r,_ in appended))

if __name__=='__main__':
    unittest.main()
