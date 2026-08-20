# -*- coding: utf-8 -*-
"""
SingleBaiduTranslator.py
纯标准库实现百度翻译 API 单测脚本（不依赖任何第三方库）。

等价于：
    from deep_translator import BaiduTranslator
    translator = BaiduTranslator(source='auto', target='zh',
                                 appid="...", appkey="...")
    print(translator.translate("リキーを発行する"))
"""

import hashlib
import json
import random
import urllib.parse
import urllib.request

API_URL = "https://fanyi-api.baidu.com/api/trans/vip/translate"


class SingleBaiduTranslator:
    """只适配百度翻译通用文本翻译 API。"""

    def __init__(self, appid, appkey, source="auto", target="zh"):
        self.appid = appid
        self.appkey = appkey
        self.source = source
        self.target = target

    def translate(self, text):
        # 官方签名规则：sign = md5(appid + q + salt + 密钥)
        salt = str(random.randint(10000, 99999))
        sign = hashlib.md5(
            (self.appid + text + salt + self.appkey).encode("utf-8")
        ).hexdigest()

        params = {
            "q": text,
            "from": self.source,
            "to": self.target,
            "appid": self.appid,
            "salt": salt,
            "sign": sign,
        }
        url = API_URL + "?" + urllib.parse.urlencode(params)

        req = urllib.request.Request(url)
        with urllib.request.urlopen(req, timeout=10) as resp:
            data = json.loads(resp.read().decode("utf-8"))

        if "error_code" in data:
            raise RuntimeError(
                f"百度翻译接口报错: code={data['error_code']}, "
                f"msg={data.get('error_msg')}"
            )

        return "".join(item["dst"] for item in data["trans_result"])


def main():
    translator = SingleBaiduTranslator(
        appid="",
        appkey="",
        source="jp",
        target="zh",
    )
    result = translator.translate("リキーを発行する")
    print(result)


if __name__ == "__main__":
    main()
