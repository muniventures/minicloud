#!/usr/bin/env python3
"""Observe provider-assigned guest addresses without competing DHCP configuration."""
import ipaddress
import json
import os
from pathlib import Path
import subprocess
import sys
import time


def verify(request):
    expected=ipaddress.IPv4Address(request['privateIp'])
    subnet=ipaddress.IPv4Network(request['subnetRange'])
    if not expected.is_private or expected not in subnet:
        raise ValueError('Invalid private network assignment')
    command='/usr/sbin/ip' if Path('/usr/sbin/ip').exists() else '/sbin/ip'
    deadline=time.monotonic()+60
    while True:
        result=subprocess.run([command,'-j','-4','address','show'],check=True,capture_output=True,timeout=10)
        for interface in json.loads(result.stdout):
            if interface.get('ifname')=='lo':
                continue
            for address in interface.get('addr_info',[]):
                if address.get('local')==str(expected):
                    return {'privateIp':str(expected),'interface':interface['ifname'],'status':'guest-address-ready'}
        if time.monotonic()>=deadline:
            raise RuntimeError('Provider private address is not active in the guest')
        time.sleep(2)


if __name__=='__main__':
    os.umask(0o077)
    try:
        with open(sys.argv[1]) as stream:
            request=json.load(stream)
        print('__MINICLOUD_DATABASE_RESULT__='+json.dumps(verify(request)))
        print('__MINICLOUD_DEPLOY_COMPLETE__='+request['operationId'])
    except Exception as error:
        print('__MINICLOUD_DATABASE_RESULT__='+json.dumps({'status':'failed','errorType':type(error).__name__}))
        raise SystemExit(1)
