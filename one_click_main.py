import os
import sys
from pathlib import Path
import re
import winreg
import shutil
from datetime import datetime
from PyTools.MyAwesomeTool.MyUtil import run_cmd, compress_folder_with_progress


PROJECT_ROOT = os.path.abspath(".")


if __name__ == '__main__':
    today = datetime.now().strftime("%Y-%m-%d")
    zip_folder = os.path.join(PROJECT_ROOT, "MuvluvUiTranslate/")
    white_list = {"BepInEx/plugins/MuvluvUiTranslate"}
    compress_folder_with_progress(zip_folder, f"{today}-girlsgarden-MuvluvMod-ui-translate-plugin.zip", white_list=white_list)
