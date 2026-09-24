"""Offline safety tests; no network access, dependency installation or real donor clones."""
from __future__ import annotations
import contextlib
import io
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
import clone_references as clone

class CloneSafetyTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='dcu-reference-tests-')
        self.base = Path(self.temporary.name)
        self.root = self.base / 'other-software'
        self.manifest = self.base / 'manifest.json'
        self.source = {'id':'S01','directory':'fixture','repository':'https://github.com/example/fixture.git',
                       'read_first':['README.md']}
        self.data = {'schema':'dcu.reference-repositories.v1','repositories':[self.source]}
        self.manifest.write_text(json.dumps(self.data))
    def tearDown(self):
        self.temporary.cleanup()
    def run_helper(self, *extra):
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            return clone.main(['--manifest',str(self.manifest),'--root',str(self.root),*extra])
    def git(self, *args):
        return subprocess.run(['git',*args],check=True,capture_output=True,text=True).stdout.strip()
    def local_repo(self, remote=None):
        p=self.root/'fixture'
        p.mkdir(parents=True)
        self.git('init','-q',str(p))
        (p/'README.md').write_text('Synthetic offline source.\n')
        self.git('-C',str(p),'add','README.md')
        self.git('-C',str(p),'-c','user.name=DCU Test','-c','user.email=test@example.invalid',
                 'commit','-qm','fixture')
        self.git('-C',str(p),'remote','add','origin',remote or self.source['repository'])
        return p
    def test_remote_normalisation(self):
        self.assertEqual(clone.canonical_remote('https://github.com/Owner/Repo.git'),'owner/repo')
        self.assertEqual(clone.canonical_remote('https://github.com/Owner/Repo/'),'owner/repo')
    def test_unsafe_remotes_rejected(self):
        for remote in ['file:///tmp/repo','--upload-pack=bad','https://evil.invalid/x/y',None]:
            with self.assertRaises(ValueError): clone.canonical_remote(remote)
    def test_unsafe_directory_rejected(self):
        self.data['repositories'][0]['directory']='../escape'
        with self.assertRaises(ValueError): clone.validate_manifest(self.data)
    def test_duplicate_ids_rejected(self):
        self.data['repositories'].append(dict(self.source,directory='another'))
        with self.assertRaises(ValueError): clone.validate_manifest(self.data)
    def test_dry_run_writes_nothing(self):
        self.assertEqual(self.run_helper('--dry-run'),0)
        self.assertFalse(self.root.exists())
    def test_clean_reference_gets_locked(self):
        p=self.local_repo()
        before=self.git('-C',str(p),'rev-parse','HEAD')
        self.assertEqual(self.run_helper(),0)
        record=json.loads((self.root/'_index/source-lock.json').read_text())['repositories']['fixture']
        self.assertEqual(record['commit'],before)
        self.assertTrue(record['read_first_present']['README.md'])
        self.assertEqual(self.git('-C',str(p),'status','--porcelain'),'')
        self.assertEqual(self.run_helper(),0)
    def test_dirty_reference_is_preserved(self):
        p=self.local_repo()
        (p/'README.md').write_text('Keep my local edit.\n')
        self.assertEqual(self.run_helper(),1)
        self.assertEqual((p/'README.md').read_text(),'Keep my local edit.\n')
        self.assertFalse((self.root/'_index/source-lock.json').exists())
    def test_wrong_origin_is_not_changed(self):
        p=self.local_repo('https://github.com/example/different.git')
        self.assertEqual(self.run_helper(),1)
        self.assertEqual(self.git('-C',str(p),'remote','get-url','origin'),
                         'https://github.com/example/different.git')
    def test_changed_locked_commit_is_not_reset(self):
        p=self.local_repo()
        self.assertEqual(self.run_helper(),0)
        lock_before=(self.root/'_index/source-lock.json').read_text()
        (p/'README.md').write_text('New commit.\n')
        self.git('-C',str(p),'add','README.md')
        self.git('-C',str(p),'-c','user.name=DCU Test','-c','user.email=test@example.invalid',
                 'commit','-qm','changed')
        after=self.git('-C',str(p),'rev-parse','HEAD')
        self.assertEqual(self.run_helper(),1)
        self.assertEqual(self.git('-C',str(p),'rev-parse','HEAD'),after)
        self.assertEqual((self.root/'_index/source-lock.json').read_text(),lock_before)
    def test_non_repository_directory_is_preserved(self):
        p=self.root/'fixture'
        p.mkdir(parents=True)
        (p/'mine.txt').write_text('Do not overwrite.')
        self.assertEqual(self.run_helper(),1)
        self.assertEqual((p/'mine.txt').read_text(),'Do not overwrite.')
    def test_another_writer_lock_is_preserved(self):
        index=self.root/'_index'
        index.mkdir(parents=True)
        guard=index/'checkout-in-progress.lock'
        guard.write_text('another writer')
        self.assertEqual(self.run_helper(),2)
        self.assertEqual(guard.read_text(),'another writer')
    def test_unknown_selection_rejected_without_writes(self):
        self.assertEqual(self.run_helper('--only','not-in-manifest'),2)
        self.assertFalse(self.root.exists())

if __name__=='__main__':
    unittest.main()
