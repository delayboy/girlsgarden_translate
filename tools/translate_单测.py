import re
from deep_translator import GoogleTranslator
import os

translator = GoogleTranslator(source='ja', target='zh-CN')
print(translator.translate("\n既にこの端末と同じプラットフォームでデータ連携を行っている場合、道具先にデータ連携を行っていた端末の データは初期化されます"))
