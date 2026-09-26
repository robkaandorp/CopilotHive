appended = open('append_acceptance_probe.py').read()
# The heredoc consumed the content; rebuild the block from the script instead.
import re
start = appended.find('appended = """')
print('script length', len(appended))
print(appended[:400])